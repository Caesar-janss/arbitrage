-- =====================================================================
-- V003 — Account state (saldo, rekonsiliasi, transfer, equity) & risk
-- ---------------------------------------------------------------------
-- Catatan arsitektur:
--   * Saldo untuk keputusan pre-trade HIDUP DI MEMORI (BalanceCache, diisi
--     User Data Stream). Tabel di sini = jejak audit + bahan rekonsiliasi.
--   * risk.halt_state = LATCH yang persisten. Engine WAJIB memuatnya saat
--     startup: restart / crash-loop tidak boleh mereset kill-switch.
-- =====================================================================
BEGIN;

CREATE TABLE acct.balance_snapshot (
    exchange_code text        NOT NULL REFERENCES ref.exchange,
    account_type  text        NOT NULL CHECK (account_type IN ('SPOT','FUTURES','UNIFIED')),
    asset_code    text        NOT NULL REFERENCES ref.asset,
    captured_at   timestamptz NOT NULL,
    free          ref.amt     NOT NULL,            -- bisa negatif di unified margin (borrow)
    locked        ref.amt     NOT NULL DEFAULT 0,
    source        text        NOT NULL CHECK (source IN ('WS','REST','PAPER')),
    PRIMARY KEY (exchange_code, account_type, asset_code, captured_at, source)
);

-- Drift antara cache memori vs kebenaran REST. Di luar toleransi -> HALT_VENUE.
CREATE TABLE acct.reconciliation (
    recon_id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    exchange_code    text        NOT NULL REFERENCES ref.exchange,
    account_type     text        NOT NULL CHECK (account_type IN ('SPOT','FUTURES','UNIFIED')),
    asset_code       text        NOT NULL REFERENCES ref.asset,
    checked_at       timestamptz NOT NULL,
    local_total      numeric(38,18) NOT NULL,
    venue_total      numeric(38,18) NOT NULL,
    tolerance        numeric(38,18) NOT NULL CHECK (tolerance >= 0),
    diff             numeric(38,18) GENERATED ALWAYS AS (venue_total - local_total) STORED,
    within_tolerance boolean        GENERATED ALWAYS AS (abs(venue_total - local_total) <= tolerance) STORED,
    action           text        NOT NULL CHECK (action IN ('NONE','RESYNC','HALT_VENUE'))
);

CREATE INDEX ix_recon_time ON acct.reconciliation (exchange_code, checked_at DESC);

-- Rebalancing antar venue. Dicatat manual/semi-manual: API key trading
-- SEHARUSNYA tidak punya izin withdraw. Biaya aktual di sini dipakai untuk
-- mengkalibrasi RebalanceCostAmortized.
CREATE TABLE acct.transfer (
    transfer_id   bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    asset_code    text        NOT NULL REFERENCES ref.asset,
    from_exchange text        NOT NULL REFERENCES ref.exchange,
    to_exchange   text        NOT NULL REFERENCES ref.exchange,
    network       text        NOT NULL,
    amount        ref.qty     NOT NULL CHECK (amount > 0),
    fee_amount    ref.qty     NOT NULL DEFAULT 0,
    fee_quote     ref.amt     NOT NULL DEFAULT 0,
    status        text        NOT NULL CHECK (status IN ('REQUESTED','IN_TRANSIT','COMPLETED','FAILED')),
    txid          text,
    requested_at  timestamptz NOT NULL,
    completed_at  timestamptz,
    CHECK (from_exchange <> to_exchange)
);

-- Equity mark-to-market per venue (termasuk unrealized & inventory).
CREATE TABLE acct.equity_snapshot (
    captured_at           timestamptz NOT NULL,
    exchange_code         text        NOT NULL REFERENCES ref.exchange,
    equity_quote          ref.amt     NOT NULL,
    inventory_value_quote ref.amt,
    is_paper              boolean     NOT NULL DEFAULT false,
    PRIMARY KEY (captured_at, exchange_code, is_paper)
);

-- ---------------------------------------------------------------------
-- Halt latch. Level:
--   NO_NEW_ENTRIES   : stop entry baru; REPAIR/UNWIND TETAP JALAN (kritis!)
--   FLATTEN_AND_STOP : aktif menutup semua eksposur lalu berhenti
-- auto_resume_at NULL = latched, hanya bisa dibuka manual.
-- Halt transien (book basi < 1 detik) cukup di memori, tidak ditulis ke sini.
-- ---------------------------------------------------------------------
CREATE TABLE risk.halt_state (
    halt_id        bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    scope          text        NOT NULL CHECK (scope IN ('GLOBAL','VENUE','PAIR')),
    scope_key      text        NOT NULL,   -- 'ALL' | 'BYBIT' | 'BTC:BINANCE.SPOT>BYBIT.SPOT'
    level          text        NOT NULL CHECK (level IN ('NO_NEW_ENTRIES','FLATTEN_AND_STOP')),
    reason_code    text        NOT NULL,
    reason         text,
    halted_at      timestamptz NOT NULL,
    auto_resume_at timestamptz,
    cleared_at     timestamptz,
    cleared_by     text,
    CHECK (scope <> 'GLOBAL' OR scope_key = 'ALL'),
    CHECK ((cleared_at IS NULL) = (cleared_by IS NULL)),
    CHECK (cleared_at IS NULL OR cleared_at >= halted_at)
);

-- Maksimal satu halt aktif per scope.
CREATE UNIQUE INDEX ux_halt_active ON risk.halt_state (scope, scope_key) WHERE cleared_at IS NULL;

CREATE TABLE risk.risk_event (
    event_id      bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id        bigint      REFERENCES trd.engine_run,
    occurred_at   timestamptz NOT NULL,
    severity      text        NOT NULL CHECK (severity IN ('INFO','WARN','HALT_PAIR','HALT_VENUE','HALT_GLOBAL')),
    event_type    text        NOT NULL CHECK (event_type IN (
                      'STALE_BOOK','SEQ_GAP','CHECKSUM_FAIL','WS_DISCONNECT','CLOCK_SKEW',
                      'RATE_LIMIT','IP_BAN','AUTH_FAIL','CONSECUTIVE_LOSS','DAILY_DRAWDOWN',
                      'RECON_DRIFT','ORDER_UNKNOWN','REPAIR_FAILED','LIQUIDATION_RISK',
                      'SANITY_PRICE','MANUAL')),
    exchange_code text        REFERENCES ref.exchange,
    instrument_id integer     REFERENCES ref.instrument,
    trade_id      bigint      REFERENCES trd.trade_group,
    duration_ms   integer     CHECK (duration_ms >= 0),     -- episode freeze/disconnect
    details       jsonb       NOT NULL DEFAULT '{}'::jsonb,
    alerted_at    timestamptz
);

CREATE INDEX ix_risk_event_time ON risk.risk_event (occurred_at DESC);
CREATE INDEX ix_risk_event_type ON risk.risk_event (event_type, occurred_at DESC);

COMMIT;
