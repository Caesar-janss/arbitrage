-- =====================================================================
-- V002 — Engine run, opportunity log, trade group (state machine),
--        order leg, fill, funding
-- ---------------------------------------------------------------------
-- ID policy: semua ID entitas hot-path (opportunity, trade, order) di-generate
-- oleh engine (Snowflake 64-bit: ms-timestamp | node | seq). Alasannya:
--   1) clientOrderId harus ada SEBELUM order dikirim (tidak bisa menunggu DB),
--   2) writer async bisa fire-and-forget tanpa RETURNING round-trip,
--   3) ID tetap time-sortable.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- Satu baris per proses engine. Memisahkan PAPER vs LIVE dan versi config,
-- sehingga PnL bisa dibandingkan antar-config (A/B parameter).
-- ---------------------------------------------------------------------
CREATE TABLE trd.engine_run (
    run_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,   -- dibuat sinkron saat startup (bukan hot-path)
    mode        text        NOT NULL CHECK (mode IN ('PAPER','LIVE')),
    started_at  timestamptz NOT NULL DEFAULT now(),
    stopped_at  timestamptz,
    stop_reason text,
    host        text        NOT NULL,
    build_sha   text        NOT NULL,
    config_hash text        NOT NULL,
    config_json jsonb       NOT NULL
);

-- ---------------------------------------------------------------------
-- Opportunity log — volume tinggi, dipartisi harian, ada retention.
-- Engine TIDAK wajib menulis semua evaluasi: cukup yang gross_edge > 0
-- (near-miss) + semua yang dieksekusi. Ditulis via COPY binary.
-- Tidak ada FK dari trade_group ke sini (agar partisi lama bisa di-DROP);
-- trade_group menyimpan snapshot ekspektasi sendiri.
-- ---------------------------------------------------------------------
CREATE TABLE trd.opportunity (
    opportunity_id        bigint      NOT NULL,
    detected_at           timestamptz NOT NULL,
    run_id                bigint      NOT NULL REFERENCES trd.engine_run,
    strategy              text        NOT NULL CHECK (strategy IN ('SPATIAL','FUNDING_PERP_PERP','FUNDING_SPOT_PERP')),
    canonical_asset       text        NOT NULL REFERENCES ref.asset,
    buy_instrument_id     integer     NOT NULL REFERENCES ref.instrument,
    sell_instrument_id    integer     NOT NULL REFERENCES ref.instrument,
    qty_base              ref.qty     NOT NULL,   -- canonical, sudah dinormalisasi ke lot kedua venue
    -- Top-of-book vs VWAP: selisihnya = "ilusi likuiditas" yang terukur
    buy_tob_px            ref.px,
    sell_tob_px           ref.px,
    buy_vwap_px           ref.px      NOT NULL,
    sell_vwap_px          ref.px      NOT NULL,
    buy_worst_px          ref.px      NOT NULL,   -- level marginal = harga limit IOC
    sell_worst_px         ref.px      NOT NULL,
    buy_levels            smallint    NOT NULL CHECK (buy_levels  > 0),
    sell_levels           smallint    NOT NULL CHECK (sell_levels > 0),
    gross_edge_bps        ref.bps     NOT NULL,   -- (sell_vwap - buy_vwap) / buy_vwap * 1e4
    fee_cost_quote        ref.amt     NOT NULL,
    slippage_buffer_quote ref.amt     NOT NULL,
    rebalance_cost_quote  ref.amt     NOT NULL,
    expected_net_quote    ref.amt     NOT NULL,
    -- Funding-only (NULL untuk SPATIAL)
    funding_income_bps_per_h ref.bps,
    breakeven_hours          numeric(12,4),
    expected_hold_hours      numeric(12,4),
    annualized_net_yield     numeric(18,8),
    -- Kualitas data saat keputusan
    buy_book_age_us       integer     NOT NULL CHECK (buy_book_age_us  >= 0),
    sell_book_age_us      integer     NOT NULL CHECK (sell_book_age_us >= 0),
    buy_book_seq          bigint,
    sell_book_seq         bigint,
    decision              text        NOT NULL CHECK (decision IN (
                              'EXECUTE','REJECT_THRESHOLD','REJECT_STALE','REJECT_INVENTORY',
                              'REJECT_RISK_HALT','REJECT_SANITY','REJECT_COOLDOWN','REJECT_MIN_NOTIONAL')),
    PRIMARY KEY (opportunity_id, detected_at),
    CHECK (buy_instrument_id <> sell_instrument_id),
    CHECK ((strategy = 'SPATIAL') = (funding_income_bps_per_h IS NULL))
) PARTITION BY RANGE (detected_at);

CREATE TABLE trd.opportunity_default PARTITION OF trd.opportunity DEFAULT;

CREATE INDEX ix_opportunity_asset_time ON trd.opportunity (canonical_asset, detected_at);
CREATE INDEX ix_opportunity_decision   ON trd.opportunity (decision, detected_at);

-- ---------------------------------------------------------------------
-- State machine Two-Legged Trade. Tabel transisi = kontrak yang HARUS
-- identik dengan TradeStateMachine di C# (ada unit test yang membandingkan).
-- ---------------------------------------------------------------------
CREATE TABLE trd.trade_state (
    state       text PRIMARY KEY,
    is_terminal boolean NOT NULL,
    description text    NOT NULL
);

INSERT INTO trd.trade_state (state, is_terminal, description) VALUES
  ('CREATED',         false, 'Keputusan diambil, inventory sudah di-reserve, order belum keluar'),
  ('IN_FLIGHT',       false, 'Order kedua kaki terkirim, menunggu laporan terminal'),
  ('COMPLETED',       true,  'SPATIAL: kedua kaki terminal, eksposur bersih ~0'),
  ('MISSED',          true,  'Kedua kaki terminal tanpa fill (atau dibatalkan sebelum kirim)'),
  ('RESIDUAL',        false, 'Kaki terminal tapi eksposur bersih != 0 (legging)'),
  ('REPAIRING',       false, 'Order perbaikan (complete / unwind / perp hedge) berjalan'),
  ('REPAIRED',        true,  'Eksposur kembali ~0 setelah perbaikan'),
  ('POSITION_OPEN',   false, 'FUNDING: posisi delta-neutral sedang ditahan'),
  ('EXITING',         false, 'FUNDING: order exit berjalan'),
  ('CLOSED',          true,  'FUNDING: posisi ditutup penuh'),
  ('STUCK',           false, 'Perbaikan gagal melewati batas waktu/percobaan -> halt + intervensi'),
  ('MANUAL_RESOLVED', true,  'Diselesaikan manual oleh operator');

CREATE TABLE trd.trade_state_transition (
    from_state text NOT NULL REFERENCES trd.trade_state,
    to_state   text NOT NULL REFERENCES trd.trade_state,
    PRIMARY KEY (from_state, to_state),
    CHECK (from_state <> to_state)
);

INSERT INTO trd.trade_state_transition (from_state, to_state) VALUES
  ('CREATED',       'IN_FLIGHT'),
  ('CREATED',       'MISSED'),          -- dibatalkan sebelum kirim (risk gate berubah)
  ('IN_FLIGHT',     'COMPLETED'),
  ('IN_FLIGHT',     'MISSED'),
  ('IN_FLIGHT',     'RESIDUAL'),
  ('IN_FLIGHT',     'POSITION_OPEN'),
  ('IN_FLIGHT',     'STUCK'),           -- status order UNKNOWN & rekonsiliasi gagal
  ('RESIDUAL',      'REPAIRING'),
  ('RESIDUAL',      'STUCK'),
  ('REPAIRING',     'REPAIRED'),
  ('REPAIRING',     'POSITION_OPEN'),   -- funding entry: kaki yang kurang berhasil dilengkapi
  ('REPAIRING',     'CLOSED'),          -- funding exit yang legging lalu diperbaiki
  ('REPAIRING',     'RESIDUAL'),        -- repair terisi sebagian -> evaluasi ulang
  ('REPAIRING',     'STUCK'),
  ('POSITION_OPEN', 'EXITING'),
  ('EXITING',       'CLOSED'),
  ('EXITING',       'RESIDUAL'),
  ('EXITING',       'STUCK'),
  ('STUCK',         'REPAIRING'),
  ('STUCK',         'MANUAL_RESOLVED');

-- ---------------------------------------------------------------------
-- Trade group = satu keputusan arbitrase (2 kaki + order repair/exit).
-- Menyimpan snapshot ekspektasi saat keputusan -> implementation shortfall
-- bisa dihitung walau partisi opportunity sudah di-drop.
-- ---------------------------------------------------------------------
CREATE TABLE trd.trade_group (
    trade_id           bigint      PRIMARY KEY,
    run_id             bigint      NOT NULL REFERENCES trd.engine_run,
    opportunity_id     bigint,                       -- soft reference (lihat catatan retention)
    opportunity_at     timestamptz,
    strategy           text        NOT NULL CHECK (strategy IN ('SPATIAL','FUNDING_PERP_PERP','FUNDING_SPOT_PERP')),
    canonical_asset    text        NOT NULL REFERENCES ref.asset,
    quote_asset        text        NOT NULL REFERENCES ref.asset,
    buy_instrument_id  integer     NOT NULL REFERENCES ref.instrument,   -- FUNDING: kaki long
    sell_instrument_id integer     NOT NULL REFERENCES ref.instrument,   -- FUNDING: kaki short
    target_qty_base    ref.qty     NOT NULL CHECK (target_qty_base > 0),
    exp_buy_vwap_px    ref.px      NOT NULL,          -- canonical px
    exp_sell_vwap_px   ref.px      NOT NULL,
    exp_fee_quote      ref.amt     NOT NULL,
    exp_net_quote      ref.amt     NOT NULL,
    state              text        NOT NULL DEFAULT 'CREATED' REFERENCES trd.trade_state,
    state_version      integer     NOT NULL DEFAULT 0 CHECK (state_version >= 0),
    state_reason       text,
    state_changed_at   timestamptz NOT NULL,
    repair_method      text CHECK (repair_method IN ('COMPLETE_SELL_LEG','COMPLETE_BUY_LEG','UNWIND','PERP_HEDGE','MANUAL')),
    created_at         timestamptz NOT NULL,
    closed_at          timestamptz,
    CHECK (buy_instrument_id <> sell_instrument_id),
    CHECK (closed_at IS NULL OR closed_at >= created_at)
);

CREATE INDEX ix_trade_run_created ON trd.trade_group (run_id, created_at);
CREATE INDEX ix_trade_closed      ON trd.trade_group (closed_at) WHERE closed_at IS NOT NULL;
CREATE INDEX ix_trade_open        ON trd.trade_group (state)
    WHERE state NOT IN ('COMPLETED','MISSED','REPAIRED','CLOSED','MANUAL_RESOLVED');

CREATE TABLE trd.trade_state_log (
    trade_id   bigint      NOT NULL REFERENCES trd.trade_group ON DELETE CASCADE,
    seq        integer     NOT NULL,       -- = state_version setelah transisi
    from_state text        REFERENCES trd.trade_state,
    to_state   text        NOT NULL REFERENCES trd.trade_state,
    changed_at timestamptz NOT NULL,
    reason     text,
    PRIMARY KEY (trade_id, seq)
);

-- ---------------------------------------------------------------------
-- Order leg. Didesain untuk UPSERT dari writer async: trigger (V004)
-- menggabungkan laporan secara MONOTON, karena WS execution report dan
-- respons REST sering tiba tidak berurutan (FILLED bisa datang sebelum ACK).
-- ---------------------------------------------------------------------
CREATE TABLE trd.order_leg (
    order_id             bigint      PRIMARY KEY,
    trade_id             bigint      NOT NULL REFERENCES trd.trade_group,
    leg_role             text        NOT NULL CHECK (leg_role IN ('ENTRY_BUY','ENTRY_SELL','REPAIR','EXIT_BUY','EXIT_SELL')),
    instrument_id        integer     NOT NULL REFERENCES ref.instrument,
    side                 text        NOT NULL CHECK (side IN ('BUY','SELL')),
    order_type           text        NOT NULL CHECK (order_type IN ('LIMIT','MARKET')),
    time_in_force        text        NOT NULL CHECK (time_in_force IN ('IOC','FOK','GTC','POST_ONLY')),
    client_order_id      text        NOT NULL UNIQUE,          -- engine-generated, global unik
    exchange_order_id    text,
    limit_px             ref.px,                               -- venue px
    req_qty_venue        ref.qty     NOT NULL CHECK (req_qty_venue > 0),
    status               text        NOT NULL CHECK (status IN (
                             'PENDING_SEND','SENT','UNKNOWN','ACKED','PARTIALLY_FILLED',
                             'FILLED','CANCELED','REJECTED','EXPIRED')),
    cum_filled_qty_venue ref.qty     NOT NULL DEFAULT 0,       -- kumulatif menurut venue
    avg_fill_px          ref.px,                               -- menurut venue
    status_conflicts     smallint    NOT NULL DEFAULT 0,       -- laporan terminal yang saling bertentangan
    reject_code          text,
    reject_reason        text,
    decided_at           timestamptz NOT NULL,                 -- clock lokal
    sent_at              timestamptz,                          -- byte keluar dari socket (lokal)
    acked_at             timestamptz,                          -- respons pertama diterima (lokal)
    venue_accept_at      timestamptz,                          -- timestamp milik venue (clock venue!)
    terminal_at          timestamptz,
    updated_at           timestamptz NOT NULL DEFAULT now(),
    CHECK (order_type = 'MARKET' OR limit_px IS NOT NULL),
    CHECK (cum_filled_qty_venue <= req_qty_venue),
    CHECK ((leg_role IN ('ENTRY_BUY','EXIT_BUY')  AND side = 'BUY')
        OR (leg_role IN ('ENTRY_SELL','EXIT_SELL') AND side = 'SELL')
        OR  leg_role = 'REPAIR')
);

CREATE INDEX ix_order_trade ON trd.order_leg (trade_id);
CREATE INDEX ix_order_live  ON trd.order_leg (updated_at)
    WHERE status IN ('PENDING_SEND','SENT','UNKNOWN','ACKED','PARTIALLY_FILLED');

-- ---------------------------------------------------------------------
-- Fill = sumber kebenaran PnL. Idempoten: (instrument, venue_trade_id) unik,
-- sehingga fill yang sama dari WS + REST reconciliation tidak tercatat dua kali
-- (writer pakai INSERT ... ON CONFLICT DO NOTHING).
-- ---------------------------------------------------------------------
CREATE TABLE trd.fill (
    fill_id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    order_id       bigint      NOT NULL REFERENCES trd.order_leg,
    instrument_id  integer     NOT NULL REFERENCES ref.instrument,   -- diisi trigger bila NULL
    venue_trade_id text        NOT NULL,
    side           text        NOT NULL CHECK (side IN ('BUY','SELL')),
    px             ref.px      NOT NULL,                             -- venue px
    qty_venue      ref.qty     NOT NULL CHECK (qty_venue > 0),
    qty_base       ref.qty     NOT NULL,                             -- trigger: qty_venue * multiplier
    quote_qty      ref.amt     NOT NULL,                             -- trigger: px * qty_venue
    fee_amount     ref.amt     NOT NULL,                             -- dalam fee_asset; negatif = rebate
    fee_asset      text        NOT NULL REFERENCES ref.asset,
    fee_quote      ref.amt     NOT NULL,                             -- fee dinilai dalam quote saat fill (engine)
    is_maker       boolean     NOT NULL,
    is_simulated   boolean     NOT NULL DEFAULT false,
    venue_ts       timestamptz NOT NULL,
    received_at    timestamptz NOT NULL,
    UNIQUE (instrument_id, venue_trade_id)
);

CREATE INDEX ix_fill_order ON trd.fill (order_id);

-- ---------------------------------------------------------------------
-- Funding: rate per settlement (interval dinamis!) + pembayaran aktual.
-- ---------------------------------------------------------------------
CREATE TABLE md.funding_rate (
    instrument_id integer        NOT NULL REFERENCES ref.instrument,
    funding_time  timestamptz    NOT NULL,          -- waktu settlement
    rate          numeric(18,10) NOT NULL,          -- fraksi per interval; + = long bayar short
    interval_h    smallint       NOT NULL CHECK (interval_h IN (1,2,4,8)),
    is_final      boolean        NOT NULL,          -- false = predicted/estimated (di-upsert terus)
    captured_at   timestamptz    NOT NULL,
    PRIMARY KEY (instrument_id, funding_time, is_final)
);

CREATE TABLE trd.funding_payment (
    funding_payment_id bigint         GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    trade_id           bigint         REFERENCES trd.trade_group,
    instrument_id      integer        NOT NULL REFERENCES ref.instrument,
    funding_time       timestamptz    NOT NULL,
    rate               numeric(18,10) NOT NULL,
    position_qty_base  ref.amt        NOT NULL,     -- signed: + long, - short
    amount_quote       ref.amt        NOT NULL,     -- + diterima, - dibayar (versi venue)
    venue_txn_id       text           NOT NULL,
    UNIQUE (instrument_id, venue_txn_id)
);

CREATE INDEX ix_funding_payment_trade ON trd.funding_payment (trade_id) WHERE trade_id IS NOT NULL;

COMMIT;
