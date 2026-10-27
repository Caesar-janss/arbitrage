-- =====================================================================
-- V001 — Schemas, domains, reference data
-- ---------------------------------------------------------------------
-- Konvensi harga & kuantitas (WAJIB dipahami sebelum menulis adapter):
--   * "venue units"     : satuan yang dipakai bursa (mis. 1000PEPEUSDT → 1 unit = 1000 PEPE).
--   * "canonical base"  : satuan aset dasar sebenarnya (PEPE, BTC).
--   * qty_base          = qty_venue * instrument.base_multiplier
--   * notional (quote)  = px_venue * qty_venue      (berlaku utk Binance/Bybit/Bitget linear & spot)
--   * canonical px      = notional / qty_base       (quote per 1 unit canonical base)
--   Semua kolom *_vwap_px di trade_group & view rpt.* memakai canonical px.
--   Kolom limit_px / px di order_leg & fill memakai venue px.
-- =====================================================================
BEGIN;

CREATE SCHEMA IF NOT EXISTS ref;   -- reference & config: venue, aset, instrumen, fee
CREATE SCHEMA IF NOT EXISTS md;    -- market data yang layak disimpan di RDBMS (funding). L2 tick TIDAK di sini.
CREATE SCHEMA IF NOT EXISTS trd;   -- opportunity, trade group (state machine), order leg, fill, funding payment
CREATE SCHEMA IF NOT EXISTS acct;  -- saldo, rekonsiliasi, transfer rebalancing, equity
CREATE SCHEMA IF NOT EXISTS risk;  -- halt latch, risk event, fungsi circuit breaker
CREATE SCHEMA IF NOT EXISTS rpt;   -- view analitik (read-only)

-- numeric(38,18): exact, cukup untuk harga meme-coin (1e-10) s.d. notional besar.
CREATE DOMAIN ref.px  AS numeric(38,18) CHECK (VALUE > 0);
CREATE DOMAIN ref.qty AS numeric(38,18) CHECK (VALUE >= 0);
CREATE DOMAIN ref.amt AS numeric(38,18);          -- signed money / signed delta
CREATE DOMAIN ref.bps AS numeric(18,6);

-- ---------------------------------------------------------------------
-- Setting numerik yang dipakai view/fungsi (bukan config engine; itu di appsettings).
-- ---------------------------------------------------------------------
CREATE TABLE ref.setting (
    key   text PRIMARY KEY,
    value numeric NOT NULL,
    note  text NOT NULL
);

INSERT INTO ref.setting (key, value, note) VALUES
  ('dust_quote',    1.0, 'Residual eksposur (nilai quote) <= ini dianggap flat'),
  ('repair_slo_ms', 500, 'Target waktu RESIDUAL -> flat untuk emergency unwind');

CREATE FUNCTION ref.fn_setting(p_key text) RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT value FROM ref.setting WHERE key = p_key
$$;

-- ---------------------------------------------------------------------
-- Venue & aset
-- ---------------------------------------------------------------------
CREATE TABLE ref.exchange (
    exchange_code text PRIMARY KEY CHECK (exchange_code ~ '^[A-Z0-9_]{2,16}$'),
    display_name  text    NOT NULL,
    is_active     boolean NOT NULL DEFAULT true
);

CREATE TABLE ref.asset (
    asset_code text PRIMARY KEY CHECK (asset_code ~ '^[A-Z0-9]{2,20}$')   -- canonical: BTC, PEPE, USDT, BNB
);

-- ---------------------------------------------------------------------
-- Instrumen. base_multiplier menyelesaikan masalah "1000PEPE" vs "PEPE":
-- tanpa ini, perbandingan harga lintas venue salah 1000x dan engine
-- akan melihat "arbitrase" palsu yang sangat besar.
-- ---------------------------------------------------------------------
CREATE TABLE ref.instrument (
    instrument_id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    exchange_code      text    NOT NULL REFERENCES ref.exchange,
    market_type        text    NOT NULL CHECK (market_type IN ('SPOT','PERP_LINEAR')),
    venue_symbol       text    NOT NULL,
    base_asset         text    NOT NULL REFERENCES ref.asset,
    quote_asset        text    NOT NULL REFERENCES ref.asset,
    base_multiplier    numeric(38,18) NOT NULL DEFAULT 1 CHECK (base_multiplier > 0),
    tick_size          ref.px  NOT NULL,
    step_size          ref.px  NOT NULL,                  -- lot step, venue units
    min_qty            ref.qty NOT NULL DEFAULT 0,        -- venue units
    min_notional       ref.qty NOT NULL DEFAULT 0,        -- quote
    funding_interval_h smallint CHECK (funding_interval_h IN (1,2,4,8)),  -- nilai terakhir; bisa berubah dinamis
    status             text    NOT NULL DEFAULT 'TRADING'
                               CHECK (status IN ('TRADING','HALTED','DELISTING','DELISTED')),
    updated_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (exchange_code, market_type, venue_symbol),
    CHECK ((market_type = 'PERP_LINEAR') = (funding_interval_h IS NOT NULL)),
    CHECK (base_asset <> quote_asset)
);

CREATE INDEX ix_instrument_canonical ON ref.instrument (base_asset, quote_asset, market_type)
    WHERE status = 'TRADING';

-- ---------------------------------------------------------------------
-- Fee schedule (SCD by effective_from). instrument_id NULL = default venue+market.
-- Nilai di sini adalah fee "yang diharapkan" untuk kalkulasi edge; fee AKTUAL
-- selalu dari trd.fill (sumber kebenaran).
-- ---------------------------------------------------------------------
CREATE TABLE ref.fee_schedule (
    fee_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    exchange_code  text    NOT NULL REFERENCES ref.exchange,
    market_type    text    NOT NULL CHECK (market_type IN ('SPOT','PERP_LINEAR')),
    instrument_id  integer REFERENCES ref.instrument,
    effective_from timestamptz NOT NULL,
    maker_bps      ref.bps NOT NULL,                     -- negatif = rebate
    taker_bps      ref.bps NOT NULL CHECK (taker_bps >= 0),
    discount_pct   numeric(9,6) NOT NULL DEFAULT 0 CHECK (discount_pct BETWEEN 0 AND 100),
    discount_asset text    REFERENCES ref.asset,          -- BNB / BGB; NULL = tanpa diskon koin
    source         text    NOT NULL DEFAULT 'API' CHECK (source IN ('API','MANUAL')),
    CHECK ((discount_pct = 0) OR (discount_asset IS NOT NULL)),
    UNIQUE NULLS NOT DISTINCT (exchange_code, market_type, instrument_id, effective_from)
);

-- Fee efektif (bps) untuk instrumen pada waktu tertentu: override per-instrumen > default venue.
CREATE FUNCTION ref.fn_effective_fee_bps(p_instrument_id integer,
                                         p_is_maker      boolean,
                                         p_at            timestamptz DEFAULT now())
RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT (CASE WHEN p_is_maker THEN f.maker_bps ELSE f.taker_bps END)
           * (1 - f.discount_pct / 100)
    FROM ref.instrument i
    JOIN ref.fee_schedule f
      ON f.exchange_code = i.exchange_code
     AND f.market_type   = i.market_type
     AND (f.instrument_id = i.instrument_id OR f.instrument_id IS NULL)
     AND f.effective_from <= p_at
    WHERE i.instrument_id = p_instrument_id
    ORDER BY (f.instrument_id IS NULL), f.effective_from DESC
    LIMIT 1
$$;

COMMIT;
