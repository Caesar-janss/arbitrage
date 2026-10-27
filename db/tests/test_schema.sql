-- =====================================================================
-- Scenario & integrity test suite untuk skema ArbEngine.
-- Jalankan via db/tests/run_tests.sh (DB baru → migrations → file ini).
-- Setiap assert gagal = RAISE EXCEPTION → psql berhenti (ON_ERROR_STOP).
--
-- Skenario (LIVE run kecuali disebut lain), T0 = 2026-10-22 01:00 UTC:
--   1001 SPATIAL bersih, buy Binance (fee BNB, 2 fill) / sell Bybit      → +0.4485
--   1002 SPATIAL legging: buy Bitget terisi (fee dlm BTC), sell Bybit 0,
--        UNWIND di Bitget dalam 220 ms                                   → −1.4988003
--   1003 SPATIAL terisi tapi slippage (fee BNB & BGB)                    → −0.41544
--   1004 SPATIAL MISSED (kedua IOC tanpa fill)                           →  0
--   1005 SPATIAL rugi tipis + laporan terminal bertentangan             → −0.190365
--   1006 FUNDING perp-perp 1000PEPE (multiplier 1000), tahan 16 jam,
--        6 funding payment, exit                                         → −1.951285
--   1007 SPATIAL STUCK: sell & repair ditolak 429, 1 fill hilang dari feed
--   2001 PAPER SPATIAL (fill simulasi)                                   → +0.948
-- =====================================================================
\set ON_ERROR_STOP on
SET client_min_messages = notice;
SET TIME ZONE 'UTC';

-- ---------------------------------------------------------------------
-- Helpers
-- ---------------------------------------------------------------------
CREATE FUNCTION pg_temp.assert_eq(p_label text, p_actual numeric, p_expected numeric,
                                  p_tol numeric DEFAULT 0.000000001)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF p_actual IS NULL AND p_expected IS NULL THEN
        RAISE NOTICE 'PASS  %  = NULL', p_label; RETURN;
    END IF;
    IF p_actual IS NULL OR p_expected IS NULL OR abs(p_actual - p_expected) > p_tol THEN
        RAISE EXCEPTION 'FAIL  %  actual=%  expected=%', p_label, p_actual, p_expected;
    END IF;
    RAISE NOTICE 'PASS  %  = %', p_label, p_actual;
END $$;

CREATE FUNCTION pg_temp.assert_true(p_label text, p_cond boolean)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF p_cond IS NOT TRUE THEN RAISE EXCEPTION 'FAIL  %  (got %)', p_label, p_cond; END IF;
    RAISE NOTICE 'PASS  %', p_label;
END $$;

CREATE FUNCTION pg_temp.assert_text(p_label text, p_actual text, p_expected text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    IF p_actual IS DISTINCT FROM p_expected THEN
        RAISE EXCEPTION 'FAIL  %  actual=%  expected=%', p_label, p_actual, p_expected;
    END IF;
    RAISE NOTICE 'PASS  %  = %', p_label, p_actual;
END $$;

CREATE FUNCTION pg_temp.expect_error(p_label text, p_sql text, p_state text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE p_sql;
    EXCEPTION WHEN OTHERS THEN
        IF SQLSTATE = p_state THEN
            RAISE NOTICE 'PASS  % → ditolak % (%)', p_label, SQLSTATE, SQLERRM;
            RETURN;
        END IF;
        RAISE EXCEPTION 'FAIL  %  expected SQLSTATE % got % (%)', p_label, p_state, SQLSTATE, SQLERRM;
    END;
    RAISE EXCEPTION 'FAIL  %  expected error % tapi statement sukses', p_label, p_state;
END $$;

CREATE FUNCTION pg_temp.inst(p_ex text, p_mt text, p_sym text) RETURNS integer
LANGUAGE sql STABLE AS $$
    SELECT instrument_id FROM ref.instrument
    WHERE exchange_code = p_ex AND market_type = p_mt AND venue_symbol = p_sym
$$;

-- ---------------------------------------------------------------------
-- Reference data
-- ---------------------------------------------------------------------
INSERT INTO ref.exchange (exchange_code, display_name) VALUES
  ('BINANCE','Binance'), ('BYBIT','Bybit'), ('BITGET','Bitget');

INSERT INTO ref.asset (asset_code) VALUES ('BTC'),('PEPE'),('USDT'),('BNB'),('BGB');

INSERT INTO ref.instrument (exchange_code, market_type, venue_symbol, base_asset, quote_asset,
                            base_multiplier, tick_size, step_size, min_notional, funding_interval_h) VALUES
  ('BINANCE','SPOT',       'BTCUSDT',      'BTC', 'USDT',    1, 0.01,       0.00001,  5, NULL),
  ('BYBIT',  'SPOT',       'BTCUSDT',      'BTC', 'USDT',    1, 0.01,       0.000001, 5, NULL),
  ('BITGET', 'SPOT',       'BTCUSDT',      'BTC', 'USDT',    1, 0.01,       0.000001, 5, NULL),
  ('BINANCE','PERP_LINEAR','1000PEPEUSDT', 'PEPE','USDT', 1000, 0.0000001,  1,        5, 8),
  ('BYBIT',  'PERP_LINEAR','1000PEPEUSDT', 'PEPE','USDT', 1000, 0.0000001,  100,      5, 4),
  ('BITGET', 'SPOT',       'PEPEUSDT',     'PEPE','USDT',    1, 0.00000001, 1,        5, NULL);

INSERT INTO ref.fee_schedule (exchange_code, market_type, instrument_id, effective_from,
                              maker_bps, taker_bps, discount_pct, discount_asset) VALUES
  ('BINANCE','SPOT',        NULL, '2026-01-01', 10,  10,  25, 'BNB'),
  ('BYBIT',  'SPOT',        NULL, '2026-01-01', 10,  10,   0, NULL),
  ('BITGET', 'SPOT',        NULL, '2026-01-01', 10,  10,  20, 'BGB'),
  ('BINANCE','PERP_LINEAR', NULL, '2026-01-01',  2,   5,  10, 'BNB'),
  ('BYBIT',  'PERP_LINEAR', NULL, '2026-01-01',  2, 5.5,   0, NULL),
  -- override VIP tier khusus BTCUSDT Binance mulai Juni
  ('BINANCE','SPOT', (SELECT pg_temp.inst('BINANCE','SPOT','BTCUSDT')), '2026-06-01', 9.5, 9.5, 25, 'BNB');

SELECT pg_temp.assert_eq('fee: Binance BTC taker (override VIP, diskon BNB)',
       ref.fn_effective_fee_bps(pg_temp.inst('BINANCE','SPOT','BTCUSDT'), false, '2026-10-22'), 7.125);
SELECT pg_temp.assert_eq('fee: Binance BTC taker sebelum override berlaku',
       ref.fn_effective_fee_bps(pg_temp.inst('BINANCE','SPOT','BTCUSDT'), false, '2026-03-01'), 7.5);
SELECT pg_temp.assert_eq('fee: Bitget spot taker (diskon BGB 20%)',
       ref.fn_effective_fee_bps(pg_temp.inst('BITGET','SPOT','BTCUSDT'), false, '2026-10-22'), 8);
SELECT pg_temp.assert_eq('fee: Binance perp maker (diskon 10%)',
       ref.fn_effective_fee_bps(pg_temp.inst('BINANCE','PERP_LINEAR','1000PEPEUSDT'), true, '2026-10-22'), 1.8);

SELECT pg_temp.expect_error('instrument: PERP wajib funding_interval_h',
  $q$INSERT INTO ref.instrument (exchange_code, market_type, venue_symbol, base_asset, quote_asset, tick_size, step_size)
     VALUES ('BYBIT','PERP_LINEAR','BTCUSDT','BTC','USDT',0.1,0.001)$q$, '23514');

-- ---------------------------------------------------------------------
-- Engine runs
-- ---------------------------------------------------------------------
INSERT INTO trd.engine_run (mode, started_at, host, build_sha, config_hash, config_json) VALUES
  ('LIVE',  '2026-10-22 00:55+00', 'vps-sg-1', 'abc1234', 'cfg-v1', '{"minProfitQuote":0.05}'),
  ('PAPER', '2026-10-22 00:55+00', 'vps-sg-1', 'abc1234', 'cfg-v2', '{"minProfitQuote":0.02,"simLatencyMs":50}');

-- ---------------------------------------------------------------------
-- Partisi opportunity
-- ---------------------------------------------------------------------
SELECT pg_temp.assert_eq('partition: buat 7 partisi harian',
       trd.fn_ensure_opportunity_partitions('2026-10-21', 7), 7);
SELECT pg_temp.assert_eq('partition: idempoten (panggilan kedua = 0)',
       trd.fn_ensure_opportunity_partitions('2026-10-21', 7), 0);

INSERT INTO trd.opportunity (opportunity_id, detected_at, run_id, strategy, canonical_asset,
    buy_instrument_id, sell_instrument_id, qty_base, buy_tob_px, sell_tob_px, buy_vwap_px, sell_vwap_px,
    buy_worst_px, sell_worst_px, buy_levels, sell_levels, gross_edge_bps, fee_cost_quote,
    slippage_buffer_quote, rebalance_cost_quote, expected_net_quote,
    buy_book_age_us, sell_book_age_us, buy_book_seq, sell_book_seq, decision)
VALUES
  (501, '2026-10-22 01:00:00+00', 1, 'SPATIAL', 'BTC', 1, 2, 0.01, 59995, 60150, 60000, 60150, 60007.5, 60150,
   2, 1, 25, 1.0515, 0.08, 0.02, 0.3485, 18000, 42000, 9911, 7712, 'EXECUTE'),
  (502, '2026-10-22 01:10:00+00', 1, 'SPATIAL', 'BTC', 1, 3, 0.02, 60000, 60030, 60004, 60020, 60010, 60015,
   3, 2, 2.666, 1.92, 0.10, 0.04, -1.74, 9000, 11000, 9950, 4410, 'REJECT_THRESHOLD'),
  (503, '2026-10-22 01:20:00+00', 1, 'SPATIAL', 'BTC', 2, 1, 0.01, 60000, 60200, 60000, 60180, 60000, 60170,
   1, 2, 30, 1.0515, 0.08, 0.02, 0.6485, 210000, 15000, 7790, 9999, 'REJECT_STALE'),
  (504, '2026-12-01 00:00:00+00', 1, 'SPATIAL', 'BTC', 2, 1, 0.01, 60000, 60100, 60000, 60090, 60000, 60090,
   1, 1, 15, 1.0515, 0.08, 0.02, -0.25, 9000, 9000, 1, 1, 'REJECT_THRESHOLD');

SELECT pg_temp.assert_text('partition: row Okt-22 masuk partisi harian',
       (SELECT tableoid::regclass::text FROM trd.opportunity WHERE opportunity_id = 501),
       'trd.opportunity_20261022');
SELECT pg_temp.assert_text('partition: row Des masuk partisi default',
       (SELECT tableoid::regclass::text FROM trd.opportunity WHERE opportunity_id = 504),
       'trd.opportunity_default');

SELECT trd.fn_ensure_opportunity_partitions('2026-09-01', 1);
SELECT pg_temp.assert_eq('retention: drop partisi > 30 hari (hanya 20260901)',
       trd.fn_drop_opportunity_partitions(30, '2026-10-22'), 1);
SELECT pg_temp.assert_true('retention: partisi 20261021 tetap ada',
       to_regclass('trd.opportunity_20261021') IS NOT NULL);

SELECT pg_temp.expect_error('opportunity: SPATIAL tidak boleh punya kolom funding',
  $q$INSERT INTO trd.opportunity (opportunity_id, detected_at, run_id, strategy, canonical_asset,
      buy_instrument_id, sell_instrument_id, qty_base, buy_vwap_px, sell_vwap_px, buy_worst_px, sell_worst_px,
      buy_levels, sell_levels, gross_edge_bps, fee_cost_quote, slippage_buffer_quote, rebalance_cost_quote,
      expected_net_quote, funding_income_bps_per_h, buy_book_age_us, sell_book_age_us, decision)
     VALUES (599, '2026-10-22 02:00+00', 1, 'SPATIAL', 'BTC', 1, 2, 0.01, 60000, 60010, 60000, 60010,
      1, 1, 1.6, 1, 0, 0, -1, 0.5, 1000, 1000, 'REJECT_THRESHOLD')$q$, '23514');

-- ---------------------------------------------------------------------
-- Trade helpers (meniru AsyncPersistenceWriter)
-- ---------------------------------------------------------------------
CREATE FUNCTION pg_temp.trade(p_id bigint, p_run bigint, p_strat text, p_asset text,
                              p_buy integer, p_sell integer, p_qty numeric,
                              p_eb numeric, p_es numeric, p_ef numeric, p_en numeric, p_at timestamptz)
RETURNS void LANGUAGE sql AS $$
    INSERT INTO trd.trade_group (trade_id, run_id, strategy, canonical_asset, quote_asset,
        buy_instrument_id, sell_instrument_id, target_qty_base,
        exp_buy_vwap_px, exp_sell_vwap_px, exp_fee_quote, exp_net_quote, state_changed_at, created_at)
    VALUES (p_id, p_run, p_strat, p_asset, 'USDT', p_buy, p_sell, p_qty, p_eb, p_es, p_ef, p_en, p_at, p_at)
$$;

CREATE FUNCTION pg_temp.trans(p_trade bigint, p_state text, p_at timestamptz, p_reason text DEFAULT NULL)
RETURNS void LANGUAGE sql AS $$
    UPDATE trd.trade_group
       SET state = p_state, state_version = state_version + 1,
           state_changed_at = p_at, state_reason = p_reason
     WHERE trade_id = p_trade
$$;

CREATE FUNCTION pg_temp.ord(p_id bigint, p_trade bigint, p_role text, p_inst integer, p_side text,
                            p_limit numeric, p_req numeric, p_at timestamptz)
RETURNS void LANGUAGE sql AS $$
    INSERT INTO trd.order_leg (order_id, trade_id, leg_role, instrument_id, side, order_type, time_in_force,
                               client_order_id, limit_px, req_qty_venue, status, decided_at, sent_at)
    VALUES (p_id, p_trade, p_role, p_inst, p_side, 'LIMIT', 'IOC', 'arb-' || p_id, p_limit, p_req,
            'SENT', p_at, p_at + interval '1 ms')
$$;

-- laporan status (WS execution report / REST response), di-merge oleh trigger
CREATE FUNCTION pg_temp.rpt(p_id bigint, p_status text, p_cum numeric, p_avg numeric,
                            p_ack timestamptz, p_reject text DEFAULT NULL)
RETURNS void LANGUAGE sql AS $$
    UPDATE trd.order_leg
       SET status = p_status, cum_filled_qty_venue = p_cum, avg_fill_px = p_avg,
           acked_at = p_ack, exchange_order_id = 'X' || p_id, reject_code = p_reject,
           terminal_at = CASE WHEN trd.fn_order_status_rank(p_status) = 9 THEN p_ack END
     WHERE order_id = p_id
$$;

CREATE FUNCTION pg_temp.fill(p_order bigint, p_tid text, p_px numeric, p_qty numeric,
                             p_fee numeric, p_fee_asset text, p_fee_quote numeric,
                             p_ts timestamptz, p_sim boolean DEFAULT false)
RETURNS void LANGUAGE sql AS $$
    INSERT INTO trd.fill (order_id, venue_trade_id, px, qty_venue, fee_amount, fee_asset, fee_quote,
                          is_maker, is_simulated, venue_ts, received_at)
    VALUES (p_order, p_tid, p_px, p_qty, p_fee, p_fee_asset, p_fee_quote, false, p_sim, p_ts, p_ts + interval '3 ms')
    ON CONFLICT (instrument_id, venue_trade_id) DO NOTHING
$$;

-- ---------------------------------------------------------------------
-- 1001 — SPATIAL bersih
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(1001, 1, 'SPATIAL', 'BTC', 1, 2, 0.01, 60000, 60150, 1.0515, 0.3485, '2026-10-22 01:00:00+00');
UPDATE trd.trade_group SET opportunity_id = 501, opportunity_at = '2026-10-22 01:00:00+00' WHERE trade_id = 1001;
SELECT pg_temp.ord(10011, 1001, 'ENTRY_BUY',  1, 'BUY',  60010, 0.01, '2026-10-22 01:00:00.001+00');
SELECT pg_temp.ord(10012, 1001, 'ENTRY_SELL', 2, 'SELL', 60140, 0.01, '2026-10-22 01:00:00.001+00');
SELECT pg_temp.trans(1001, 'IN_FLIGHT', '2026-10-22 01:00:00.002+00');
-- WS FILLED datang duluan...
SELECT pg_temp.rpt(10011, 'FILLED', 0.01, 60000, '2026-10-22 01:00:00.031+00');
SELECT pg_temp.fill(10011, '1001-1', 59995,   0.006, 0.00045, 'BNB', 0.27, '2026-10-22 01:00:00.030+00');
SELECT pg_temp.fill(10011, '1001-2', 60007.5, 0.004, 0.0003,  'BNB', 0.18, '2026-10-22 01:00:00.030+00');
-- ...lalu respons REST ACK terlambat (cum=0). Harus TIDAK menurunkan status/cum/acked_at.
SELECT pg_temp.rpt(10011, 'ACKED', 0, NULL, '2026-10-22 01:00:00.035+00');
SELECT pg_temp.rpt(10012, 'FILLED', 0.01, 60150, '2026-10-22 01:00:00.045+00');
SELECT pg_temp.fill(10012, '1001-3', 60150, 0.01, 0.6015, 'USDT', 0.6015, '2026-10-22 01:00:00.044+00');
SELECT pg_temp.trans(1001, 'COMPLETED', '2026-10-22 01:00:00.060+00');

-- duplikat fill dari REST reconciliation → harus diabaikan
SELECT pg_temp.fill(10011, '1001-1', 59995, 0.006, 0.00045, 'BNB', 0.27, '2026-10-22 01:00:00.030+00');

-- UPSERT path asli (INSERT ... ON CONFLICT DO UPDATE) dengan laporan basi
INSERT INTO trd.order_leg (order_id, trade_id, leg_role, instrument_id, side, order_type, time_in_force,
                           client_order_id, limit_px, req_qty_venue, status, cum_filled_qty_venue, decided_at)
VALUES (10012, 1001, 'ENTRY_SELL', 2, 'SELL', 'LIMIT', 'IOC', 'arb-10012', 60140, 0.01, 'ACKED', 0,
        '2026-10-22 01:00:00.001+00')
ON CONFLICT (order_id) DO UPDATE
   SET status = EXCLUDED.status, cum_filled_qty_venue = EXCLUDED.cum_filled_qty_venue;

-- ---------------------------------------------------------------------
-- 1002 — legging → UNWIND
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(1002, 1, 'SPATIAL', 'BTC', 3, 2, 0.01, 60000, 60120, 1.0812, 0.0688, '2026-10-22 02:00:00+00');
SELECT pg_temp.ord(10021, 1002, 'ENTRY_BUY',  3, 'BUY',  60005, 0.01, '2026-10-22 02:00:00.001+00');
SELECT pg_temp.ord(10022, 1002, 'ENTRY_SELL', 2, 'SELL', 60110, 0.01, '2026-10-22 02:00:00.001+00');
SELECT pg_temp.trans(1002, 'IN_FLIGHT', '2026-10-22 02:00:00.002+00');
SELECT pg_temp.rpt(10021, 'FILLED', 0.01, 60000, '2026-10-22 02:00:00.040+00');
SELECT pg_temp.fill(10021, '1002-1', 60000, 0.01, 0.00001, 'BTC', 0.6, '2026-10-22 02:00:00.039+00');
SELECT pg_temp.rpt(10022, 'CANCELED', 0, NULL, '2026-10-22 02:00:00.068+00');
SELECT pg_temp.trans(1002, 'RESIDUAL',  '2026-10-22 02:00:00.070+00', 'sell leg IOC 0% fill');
UPDATE trd.trade_group SET repair_method = 'UNWIND' WHERE trade_id = 1002;
SELECT pg_temp.trans(1002, 'REPAIRING', '2026-10-22 02:00:00.075+00', 'unwind lebih murah dari complete');
SELECT pg_temp.ord(10023, 1002, 'REPAIR', 3, 'SELL', 59900, 0.00999, '2026-10-22 02:00:00.074+00');
SELECT pg_temp.rpt(10023, 'FILLED', 0.00999, 59970, '2026-10-22 02:00:00.280+00');
SELECT pg_temp.fill(10023, '1002-2', 59970, 0.00999, 0.5991003, 'USDT', 0.5991003, '2026-10-22 02:00:00.279+00');
SELECT pg_temp.trans(1002, 'REPAIRED',  '2026-10-22 02:00:00.290+00');

-- ---------------------------------------------------------------------
-- 1003 — slippage, fee BNB & BGB
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(1003, 1, 'SPATIAL', 'BTC', 1, 3, 0.005, 60000, 60080, 0.465, 0.1, '2026-10-22 03:00:00+00');
SELECT pg_temp.ord(10031, 1003, 'ENTRY_BUY',  1, 'BUY',  60060, 0.005, '2026-10-22 03:00:00.001+00');
SELECT pg_temp.ord(10032, 1003, 'ENTRY_SELL', 3, 'SELL', 60050, 0.005, '2026-10-22 03:00:00.001+00');
SELECT pg_temp.trans(1003, 'IN_FLIGHT', '2026-10-22 03:00:00.002+00');
SELECT pg_temp.rpt(10031, 'FILLED', 0.005, 60050, '2026-10-22 03:00:00.033+00');
SELECT pg_temp.fill(10031, '1003-1', 60050, 0.005, 0.000375, 'BNB', 0.2252,   '2026-10-22 03:00:00.032+00');
SELECT pg_temp.rpt(10032, 'FILLED', 0.005, 60060, '2026-10-22 03:00:00.051+00');
SELECT pg_temp.fill(10032, '1003-2', 60060, 0.003, 0.18,     'BGB', 0.144144, '2026-10-22 03:00:00.050+00');
SELECT pg_temp.fill(10032, '1003-3', 60060, 0.002, 0.12,     'BGB', 0.096096, '2026-10-22 03:00:00.050+00');
SELECT pg_temp.trans(1003, 'COMPLETED', '2026-10-22 03:00:00.080+00');

-- ---------------------------------------------------------------------
-- 1004 — MISSED
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(1004, 1, 'SPATIAL', 'BTC', 2, 1, 0.01, 60000, 60100, 1.35, 0.2, '2026-10-22 04:00:00+00');
SELECT pg_temp.ord(10041, 1004, 'ENTRY_BUY',  2, 'BUY',  60005, 0.01, '2026-10-22 04:00:00.001+00');
SELECT pg_temp.ord(10042, 1004, 'ENTRY_SELL', 1, 'SELL', 60095, 0.01, '2026-10-22 04:00:00.001+00');
SELECT pg_temp.trans(1004, 'IN_FLIGHT', '2026-10-22 04:00:00.002+00');
SELECT pg_temp.rpt(10041, 'CANCELED', 0, NULL, '2026-10-22 04:00:00.040+00');
SELECT pg_temp.rpt(10042, 'EXPIRED',  0, NULL, '2026-10-22 04:00:00.035+00');
SELECT pg_temp.trans(1004, 'MISSED', '2026-10-22 04:00:00.050+00');

-- ---------------------------------------------------------------------
-- 1005 — rugi tipis + laporan terminal bertentangan (FILLED lalu CANCELED)
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(1005, 1, 'SPATIAL', 'BTC', 2, 1, 0.002, 60100, 60130, 0.21, 0.02, '2026-10-22 05:00:00+00');
SELECT pg_temp.ord(10051, 1005, 'ENTRY_BUY',  2, 'BUY',  60105, 0.002, '2026-10-22 05:00:00.001+00');
SELECT pg_temp.ord(10052, 1005, 'ENTRY_SELL', 1, 'SELL', 60100, 0.002, '2026-10-22 05:00:00.001+00');
SELECT pg_temp.trans(1005, 'IN_FLIGHT', '2026-10-22 05:00:00.002+00');
SELECT pg_temp.rpt(10051, 'FILLED', 0.002, 60100, '2026-10-22 05:00:00.041+00');
SELECT pg_temp.fill(10051, '1005-1', 60100, 0.002, 0.1202,  'USDT', 0.1202,   '2026-10-22 05:00:00.040+00');
SELECT pg_temp.rpt(10052, 'FILLED', 0.002, 60110, '2026-10-22 05:00:00.030+00');
SELECT pg_temp.fill(10052, '1005-2', 60110, 0.002, 0.00015, 'BNB',  0.090165, '2026-10-22 05:00:00.029+00');
SELECT pg_temp.rpt(10052, 'CANCELED', 0.002, 60110, '2026-10-22 05:00:00.032+00');
SELECT pg_temp.trans(1005, 'COMPLETED', '2026-10-22 05:00:00.055+00');

-- ---------------------------------------------------------------------
-- 1006 — FUNDING perp-perp 1000PEPE: long Bybit (funding rendah), short Binance (funding tinggi)
-- qty venue 100000 unit "1000PEPE" = 100,000,000 PEPE canonical
-- ---------------------------------------------------------------------
INSERT INTO md.funding_rate (instrument_id, funding_time, rate, interval_h, is_final, captured_at) VALUES
  (4, '2026-10-22 08:00+00', 0.0001,  8, true,  '2026-10-22 08:00:01+00'),
  (4, '2026-10-22 16:00+00', 0.0001,  8, true,  '2026-10-22 16:00:01+00'),
  (4, '2026-10-23 00:00+00', 0.00008, 8, false, '2026-10-22 17:29:00+00'),
  (5, '2026-10-22 04:00+00', 0.00002, 4, true,  '2026-10-22 04:00:01+00'),
  (5, '2026-10-22 08:00+00', 0.00002, 4, true,  '2026-10-22 08:00:01+00'),
  (5, '2026-10-22 12:00+00', 0.00002, 4, true,  '2026-10-22 12:00:01+00'),
  (5, '2026-10-22 16:00+00', 0.00002, 4, true,  '2026-10-22 16:00:01+00');

SELECT pg_temp.trade(1006, 1, 'FUNDING_PERP_PERP', 'PEPE', 5, 4, 100000000,
                     0.000012, 0.000012006, 2.4, 0.5, '2026-10-22 01:30:00+00');
SELECT pg_temp.ord(10061, 1006, 'ENTRY_BUY',  5, 'BUY',  0.0120010, 100000, '2026-10-22 01:30:00.001+00');
SELECT pg_temp.ord(10062, 1006, 'ENTRY_SELL', 4, 'SELL', 0.0120050, 100000, '2026-10-22 01:30:00.001+00');
SELECT pg_temp.trans(1006, 'IN_FLIGHT', '2026-10-22 01:30:00.002+00');
SELECT pg_temp.rpt(10061, 'FILLED', 100000, 0.012,    '2026-10-22 01:30:00.040+00');
SELECT pg_temp.fill(10061, '1006-1', 0.012,    100000, 0.66,       'USDT', 0.66,    '2026-10-22 01:30:00.039+00');
SELECT pg_temp.rpt(10062, 'FILLED', 100000, 0.012006, '2026-10-22 01:30:00.036+00');
SELECT pg_temp.fill(10062, '1006-2', 0.012006, 100000, 0.00090045, 'BNB',  0.54027, '2026-10-22 01:30:00.035+00');
SELECT pg_temp.trans(1006, 'POSITION_OPEN', '2026-10-22 01:30:00.050+00');

INSERT INTO trd.funding_payment (trade_id, instrument_id, funding_time, rate, position_qty_base, amount_quote, venue_txn_id) VALUES
  (1006, 5, '2026-10-22 04:00+00', 0.00002,  100000000, -0.024,   'BYB-F-0400'),
  (1006, 4, '2026-10-22 08:00+00', 0.0001,  -100000000,  0.12006, 'BIN-F-0800'),
  (1006, 5, '2026-10-22 08:00+00', 0.00002,  100000000, -0.024,   'BYB-F-0800'),
  (1006, 5, '2026-10-22 12:00+00', 0.00002,  100000000, -0.024,   'BYB-F-1200'),
  (1006, 4, '2026-10-22 16:00+00', 0.0001,  -100000000,  0.12006, 'BIN-F-1600'),
  (1006, 5, '2026-10-22 16:00+00', 0.00002,  100000000, -0.024,   'BYB-F-1600');

SELECT pg_temp.trans(1006, 'EXITING', '2026-10-22 17:30:00+00', 'spread funding menyempit');
SELECT pg_temp.ord(10063, 1006, 'EXIT_SELL', 5, 'SELL', 0.011940, 100000, '2026-10-22 17:30:00.001+00');
SELECT pg_temp.ord(10064, 1006, 'EXIT_BUY',  4, 'BUY',  0.011960, 100000, '2026-10-22 17:30:00.001+00');
SELECT pg_temp.rpt(10063, 'FILLED', 100000, 0.01195,  '2026-10-22 17:30:00.041+00');
SELECT pg_temp.fill(10063, '1006-3', 0.01195,  100000, 0.65725,    'USDT', 0.65725,  '2026-10-22 17:30:00.040+00');
SELECT pg_temp.rpt(10064, 'FILLED', 100000, 0.011953, '2026-10-22 17:30:00.037+00');
SELECT pg_temp.fill(10064, '1006-4', 0.011953, 100000, 0.0008965,  'BNB',  0.537885, '2026-10-22 17:30:00.036+00');
SELECT pg_temp.trans(1006, 'CLOSED', '2026-10-22 17:30:00.100+00');

-- ---------------------------------------------------------------------
-- 1007 — STUCK: sell & repair ditolak 429, satu pesan fill hilang dari feed
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(1007, 1, 'SPATIAL', 'BTC', 1, 2, 0.004, 60000, 60140, 0.42, 0.1, '2026-10-22 07:00:00+00');
SELECT pg_temp.ord(10071, 1007, 'ENTRY_BUY',  1, 'BUY',  60010, 0.004, '2026-10-22 07:00:00.001+00');
SELECT pg_temp.ord(10072, 1007, 'ENTRY_SELL', 2, 'SELL', 60130, 0.004, '2026-10-22 07:00:00.001+00');
SELECT pg_temp.trans(1007, 'IN_FLIGHT', '2026-10-22 07:00:00.002+00');
SELECT pg_temp.rpt(10071, 'FILLED', 0.004, 60000, '2026-10-22 07:00:00.031+00');
SELECT pg_temp.fill(10071, '1007-1', 60000, 0.003, 0.000225, 'BNB', 0.135, '2026-10-22 07:00:00.030+00');
SELECT pg_temp.rpt(10072, 'REJECTED', 0, NULL, '2026-10-22 07:00:00.025+00', '429');
SELECT pg_temp.trans(1007, 'RESIDUAL',  '2026-10-22 07:00:00.040+00', 'sell leg rejected 429');
UPDATE trd.trade_group SET repair_method = 'COMPLETE_SELL_LEG' WHERE trade_id = 1007;
SELECT pg_temp.trans(1007, 'REPAIRING', '2026-10-22 07:00:00.045+00');
SELECT pg_temp.ord(10073, 1007, 'REPAIR', 2, 'SELL', 59900, 0.004, '2026-10-22 07:00:00.044+00');
SELECT pg_temp.rpt(10073, 'REJECTED', 0, NULL, '2026-10-22 07:00:00.090+00', '429');
SELECT pg_temp.trans(1007, 'STUCK', '2026-10-22 07:00:00.600+00', 'repair gagal: venue rate-limited');

INSERT INTO risk.risk_event (run_id, occurred_at, severity, event_type, exchange_code, trade_id, details) VALUES
  (1, '2026-10-22 07:00:00.025+00', 'HALT_VENUE',  'RATE_LIMIT',    'BYBIT', 1007, '{"http":429,"usedWeight":null}'),
  (1, '2026-10-22 07:00:00.600+00', 'HALT_GLOBAL', 'REPAIR_FAILED', 'BYBIT', 1007, '{"attempts":1}');

-- ---------------------------------------------------------------------
-- 2001 — PAPER
-- ---------------------------------------------------------------------
SELECT pg_temp.trade(2001, 2, 'SPATIAL', 'BTC', 1, 2, 0.01, 60000, 60200, 1.052, 0.9, '2026-10-22 09:00:00+00');
SELECT pg_temp.ord(20011, 2001, 'ENTRY_BUY',  1, 'BUY',  60010, 0.01, '2026-10-22 09:00:00.001+00');
SELECT pg_temp.ord(20012, 2001, 'ENTRY_SELL', 2, 'SELL', 60190, 0.01, '2026-10-22 09:00:00.001+00');
SELECT pg_temp.trans(2001, 'IN_FLIGHT', '2026-10-22 09:00:00.002+00');
SELECT pg_temp.rpt(20011, 'FILLED', 0.01, 60000, '2026-10-22 09:00:00.051+00');
SELECT pg_temp.fill(20011, 'SIM-2001-1', 60000, 0.01, 0.00075, 'BNB',  0.45,  '2026-10-22 09:00:00.051+00', true);
SELECT pg_temp.rpt(20012, 'FILLED', 0.01, 60200, '2026-10-22 09:00:00.052+00');
SELECT pg_temp.fill(20012, 'SIM-2001-2', 60200, 0.01, 0.602,   'USDT', 0.602, '2026-10-22 09:00:00.052+00', true);
SELECT pg_temp.trans(2001, 'COMPLETED', '2026-10-22 09:00:00.060+00');

-- ---------------------------------------------------------------------
-- Akun
-- ---------------------------------------------------------------------
INSERT INTO acct.balance_snapshot (exchange_code, account_type, asset_code, captured_at, free, locked, source) VALUES
  ('BINANCE','SPOT','BTC', '2026-10-22 00:00+00', 0.10, 0,    'REST'),
  ('BINANCE','SPOT','BTC', '2026-10-22 08:00+00', 0.11, 0,    'WS'),
  ('BYBIT',  'SPOT','BTC', '2026-10-22 08:00+00', 0.085, 0.005,'WS'),
  ('BITGET', 'SPOT','BTC', '2026-10-22 08:00+00', 0.05, 0,    'WS'),
  ('BINANCE','SPOT','BTC', '2026-10-22 09:00+00', 1.00, 0,    'PAPER');

INSERT INTO acct.reconciliation (exchange_code, account_type, asset_code, checked_at,
                                 local_total, venue_total, tolerance, action) VALUES
  ('BINANCE','SPOT','BTC','2026-10-22 08:05+00', 0.11, 0.111, 0.0001, 'HALT_VENUE'),
  ('BYBIT',  'SPOT','BTC','2026-10-22 08:05+00', 0.09, 0.09,  0.0001, 'NONE');

-- =====================================================================
-- ASSERTIONS
-- =====================================================================
\echo '--- order merge & fill integrity ---'
SELECT pg_temp.assert_text('merge: ACK terlambat tidak menurunkan FILLED',
       (SELECT status FROM trd.order_leg WHERE order_id = 10011), 'FILLED');
SELECT pg_temp.assert_eq('merge: cum_filled monoton (tetap 0.01)',
       (SELECT cum_filled_qty_venue FROM trd.order_leg WHERE order_id = 10011), 0.01);
SELECT pg_temp.assert_true('merge: acked_at first-write-wins (31 ms)',
       (SELECT acked_at = '2026-10-22 01:00:00.031+00' FROM trd.order_leg WHERE order_id = 10011));
SELECT pg_temp.assert_text('upsert ON CONFLICT: status tetap FILLED',
       (SELECT status FROM trd.order_leg WHERE order_id = 10012), 'FILLED');
SELECT pg_temp.assert_eq('fill: duplikat diabaikan (2 fill di order 10011)',
       (SELECT count(*) FROM trd.fill WHERE order_id = 10011), 2);
SELECT pg_temp.assert_eq('fill: qty_base = qty_venue × 1000 (1000PEPE)',
       (SELECT qty_base FROM trd.fill WHERE venue_trade_id = '1006-1'), 100000000);
SELECT pg_temp.assert_eq('fill: quote_qty = px × qty_venue',
       (SELECT quote_qty FROM trd.fill WHERE venue_trade_id = '1006-1'), 1200);
SELECT pg_temp.assert_eq('merge: konflik terminal tercatat (10052)',
       (SELECT status_conflicts FROM trd.order_leg WHERE order_id = 10052), 1);
SELECT pg_temp.assert_text('merge: status terminal pertama dipertahankan',
       (SELECT status FROM trd.order_leg WHERE order_id = 10052), 'FILLED');

\echo '--- state machine audit trail ---'
SELECT pg_temp.assert_text('state log 1002 urutan lengkap',
       (SELECT string_agg(to_state, '>' ORDER BY seq) FROM trd.trade_state_log WHERE trade_id = 1002),
       'CREATED>IN_FLIGHT>RESIDUAL>REPAIRING>REPAIRED');
SELECT pg_temp.assert_true('closed_at otomatis = waktu transisi terminal',
       (SELECT closed_at = '2026-10-22 01:00:00.060+00' FROM trd.trade_group WHERE trade_id = 1001));
SELECT pg_temp.assert_true('STUCK bukan terminal → closed_at NULL',
       (SELECT closed_at IS NULL FROM trd.trade_group WHERE trade_id = 1007));

\echo '--- PnL ledger ---'
SELECT pg_temp.assert_eq('1001 realized',  (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1001), 0.4485);
SELECT pg_temp.assert_eq('1001 entry buy VWAP dari 2 fill', (SELECT entry_buy_vwap_px FROM rpt.v_trade_ledger WHERE trade_id = 1001), 60000);
SELECT pg_temp.assert_eq('1002 realized (unwind)', (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1002), -1.4988003);
SELECT pg_temp.assert_eq('1002 net_base_delta = 0 (fee base ikut dihitung)',
       (SELECT net_base_delta FROM rpt.v_trade_ledger WHERE trade_id = 1002), 0);
SELECT pg_temp.assert_eq('1002 fees_total', (SELECT fees_total_quote FROM rpt.v_trade_ledger WHERE trade_id = 1002), 1.1991003);
SELECT pg_temp.assert_eq('1002 time_to_flat_ms', (SELECT time_to_flat_ms FROM rpt.v_trade_ledger WHERE trade_id = 1002), 220);
SELECT pg_temp.assert_eq('1003 realized', (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1003), -0.41544);
SELECT pg_temp.assert_eq('1003 buy slippage bps', (SELECT buy_slippage_bps FROM rpt.v_trade_ledger WHERE trade_id = 1003), 8.3333333333, 0.000001);
SELECT pg_temp.assert_eq('1003 sell slippage bps', (SELECT sell_slippage_bps FROM rpt.v_trade_ledger WHERE trade_id = 1003), 3.3288948069, 0.000001);
SELECT pg_temp.assert_eq('1003 shortfall', (SELECT shortfall_quote FROM rpt.v_trade_ledger WHERE trade_id = 1003), 0.51544);
SELECT pg_temp.assert_eq('1004 MISSED realized = 0', (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1004), 0);
SELECT pg_temp.assert_eq('1005 realized', (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1005), -0.190365);
SELECT pg_temp.assert_eq('1006 funding', (SELECT funding_quote FROM rpt.v_trade_ledger WHERE trade_id = 1006), 0.14412);
SELECT pg_temp.assert_eq('1006 realized (fee 4 taker > funding)', (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1006), -1.951285);
SELECT pg_temp.assert_eq('1006 entry long VWAP canonical', (SELECT entry_buy_vwap_px FROM rpt.v_trade_ledger WHERE trade_id = 1006), 0.000012);
SELECT pg_temp.assert_true('1007 tidak flat & tidak realized',
       (SELECT NOT is_flat AND realized_pnl_quote IS NULL FROM rpt.v_trade_ledger WHERE trade_id = 1007));
SELECT pg_temp.assert_eq('1007 residual (base)', (SELECT net_base_delta FROM rpt.v_trade_ledger WHERE trade_id = 1007), 0.003);
SELECT pg_temp.assert_eq('1007 est pnl', (SELECT est_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 1007), -0.135);
SELECT pg_temp.assert_eq('2001 PAPER realized', (SELECT realized_pnl_quote FROM rpt.v_trade_ledger WHERE trade_id = 2001), 0.948);

\echo '--- daily / funnel / execution / legging / funding / inventory ---'
SELECT pg_temp.assert_eq('daily LIVE SPATIAL realized',
       (SELECT realized_pnl_quote FROM rpt.v_daily_pnl WHERE day_utc = '2026-10-22' AND mode = 'LIVE' AND strategy = 'SPATIAL'), -1.6561053);
SELECT pg_temp.assert_eq('daily LIVE SPATIAL win_rate (missed dikecualikan)',
       (SELECT win_rate FROM rpt.v_daily_pnl WHERE day_utc = '2026-10-22' AND mode = 'LIVE' AND strategy = 'SPATIAL'), 0.25);
SELECT pg_temp.assert_eq('daily LIVE SPATIAL capture_ratio_filled',
       (SELECT capture_ratio_filled FROM rpt.v_daily_pnl WHERE day_utc = '2026-10-22' AND mode = 'LIVE' AND strategy = 'SPATIAL'), -3.0823, 0.00005);
SELECT pg_temp.assert_eq('funnel: REJECT_STALE terhitung',
       (SELECT n FROM rpt.v_opportunity_funnel WHERE day_utc = '2026-10-22' AND decision = 'REJECT_STALE'), 1);
SELECT pg_temp.assert_eq('funnel: ilusi likuiditas opp 501 (bps)',
       (SELECT avg_liquidity_illusion_bps FROM rpt.v_opportunity_funnel WHERE day_utc = '2026-10-22' AND decision = 'EXECUTE'), 0.835, 0.0000001);
SELECT pg_temp.assert_eq('exec quality: 1 fill mismatch di Binance spot LIVE',
       (SELECT n_fill_mismatch FROM rpt.v_execution_quality WHERE mode = 'LIVE' AND exchange_code = 'BINANCE' AND market_type = 'SPOT'), 1);
SELECT pg_temp.assert_eq('integrity: 10071 kehilangan 0.001',
       (SELECT missing_qty_venue FROM rpt.v_order_integrity WHERE order_id = 10071), 0.001);
SELECT pg_temp.assert_text('integrity: 10052 status conflict',
       (SELECT issue FROM rpt.v_order_integrity WHERE order_id = 10052), 'STATUS_CONFLICT');
SELECT pg_temp.assert_true('legging: 1002 dalam SLO 500 ms',
       (SELECT NOT slo_breached FROM rpt.v_legging_incidents WHERE trade_id = 1002));
SELECT pg_temp.assert_true('legging: 1007 STUCK = SLO breach',
       (SELECT slo_breached AND had_stuck FROM rpt.v_legging_incidents WHERE trade_id = 1007));
SELECT pg_temp.assert_eq('funding: hold hours', (SELECT hold_hours FROM rpt.v_funding_positions WHERE trade_id = 1006), 16.0000139, 0.0000001);
SELECT pg_temp.assert_eq('funding: yield funding annualized ~6.58%',
       (SELECT funding_yield_annualized FROM rpt.v_funding_positions WHERE trade_id = 1006), 0.0657547, 0.0000005);
SELECT pg_temp.assert_true('funding: funding TIDAK menutup fee',
       (SELECT NOT funding_covered_fees FROM rpt.v_funding_positions WHERE trade_id = 1006));
SELECT pg_temp.assert_eq('inventory: share Binance BTC (live) = 0.44',
       (SELECT share_of_asset FROM rpt.v_inventory WHERE exchange_code = 'BINANCE' AND asset_code = 'BTC' AND NOT is_paper), 0.44);
SELECT pg_temp.assert_eq('inventory: paper terpisah (share 1.0)',
       (SELECT share_of_asset FROM rpt.v_inventory WHERE exchange_code = 'BINANCE' AND asset_code = 'BTC' AND is_paper), 1);
SELECT pg_temp.assert_true('recon: drift di luar toleransi terdeteksi',
       (SELECT NOT within_tolerance AND diff = 0.001 FROM acct.reconciliation WHERE exchange_code = 'BINANCE'));
SELECT pg_temp.assert_eq('integrity: tepat 2 order bermasalah (late ACK bukan konflik)',
       (SELECT count(*) FROM rpt.v_order_integrity), 2);
SELECT pg_temp.assert_eq('funding: net APR (negatif karena 4x taker fee)',
       (SELECT net_yield_annualized FROM rpt.v_funding_positions WHERE trade_id = 1006), -0.8902730, 0.0000005);
SELECT pg_temp.assert_eq('open exposure LIVE: hanya 1007',
       (SELECT count(*) FROM rpt.v_open_exposure WHERE mode = 'LIVE'), 1);

\echo '--- risk functions ---'
SELECT pg_temp.assert_eq('streak LIVE semua strategi = 4 (MISSED dilewati)', risk.fn_consecutive_losses('LIVE'), 4);
SELECT pg_temp.assert_eq('streak LIVE SPATIAL = 3', risk.fn_consecutive_losses('LIVE', 'SPATIAL'), 3);
SELECT pg_temp.assert_eq('streak PAPER = 0', risk.fn_consecutive_losses('PAPER'), 0);

SELECT pg_temp.assert_eq('daily UTC total (realized + open estimate)',
       (SELECT total FROM risk.fn_daily_pnl('2026-10-22', 'UTC', 'LIVE')), -3.7423903);
SELECT pg_temp.assert_eq('daily UTC n_trades', (SELECT n_trades FROM risk.fn_daily_pnl('2026-10-22', 'UTC', 'LIVE')), 7);
SELECT pg_temp.assert_eq('daily WIB: 1006 (closed 00:30 WIB besoknya) tidak masuk',
       (SELECT total FROM risk.fn_daily_pnl('2026-10-22', 'Asia/Jakarta', 'LIVE')), -1.7911053);

-- halt latch & clear
SELECT risk.fn_latch_halt('GLOBAL','ALL','NO_NEW_ENTRIES','CONSECUTIVE_LOSS','3 rugi berturut', NULL, '2026-10-22 05:00:01+00') AS cl_halt \gset
SELECT pg_temp.assert_true('clear halt CONSECUTIVE_LOSS', risk.fn_clear_halt(:cl_halt, 'buddy', '2026-10-22 06:00+00'));
SELECT pg_temp.assert_true('clear halt kedua kali = false', NOT risk.fn_clear_halt(:cl_halt, 'buddy', '2026-10-22 06:01+00'));
SELECT pg_temp.assert_eq('streak setelah reset operator = 1 (hanya 1006)', risk.fn_consecutive_losses('LIVE'), 1);

SELECT risk.fn_latch_halt('VENUE','BYBIT','NO_NEW_ENTRIES','RATE_LIMIT','HTTP 429',
                          '2026-10-22 07:05+00', '2026-10-22 07:00:00.025+00') AS venue_halt \gset
SELECT risk.fn_latch_halt('GLOBAL','ALL','NO_NEW_ENTRIES','REPAIR_FAILED','trade 1007 stuck',
                          NULL, '2026-10-22 07:00:00.600+00') AS g_halt \gset
SELECT pg_temp.assert_eq('latch idempoten + eskalasi ke FLATTEN',
       risk.fn_latch_halt('GLOBAL','ALL','FLATTEN_AND_STOP','MANUAL','operator flatten'), :g_halt);
SELECT risk.fn_latch_halt('GLOBAL','ALL','NO_NEW_ENTRIES','REPAIR_FAILED');
SELECT pg_temp.assert_text('latch tidak bisa turun level',
       (SELECT level FROM risk.halt_state WHERE halt_id = :g_halt), 'FLATTEN_AND_STOP');
SELECT pg_temp.assert_eq('active halts: hanya GLOBAL (venue halt sudah auto-resume)',
       (SELECT count(*) FROM risk.v_active_halts), 1);
SELECT pg_temp.expect_error('halt: dua halt aktif di scope sama ditolak',
  $q$INSERT INTO risk.halt_state (scope, scope_key, level, reason_code, halted_at)
     VALUES ('GLOBAL','ALL','NO_NEW_ENTRIES','MANUAL', now())$q$, '23505');
SELECT pg_temp.expect_error('halt: clear tanpa nama operator ditolak',
  format('SELECT risk.fn_clear_halt(%s, %L)', :g_halt, ''), 'P0001');

SELECT pg_temp.assert_true('breaker cap 1000: drawdown 0.374% tidak breach',
       (SELECT NOT breached FROM risk.fn_evaluate_circuit_breaker(1000, 1.0, 3, '2026-10-22') WHERE rule = 'DAILY_DRAWDOWN_PCT'));
SELECT pg_temp.assert_eq('breaker cap 1000: nilai drawdown %',
       (SELECT current_value FROM risk.fn_evaluate_circuit_breaker(1000, 1.0, 3, '2026-10-22') WHERE rule = 'DAILY_DRAWDOWN_PCT'), 0.374239);
SELECT pg_temp.assert_true('breaker cap 300: drawdown breach',
       (SELECT breached FROM risk.fn_evaluate_circuit_breaker(300, 1.0, 3, '2026-10-22') WHERE rule = 'DAILY_DRAWDOWN_PCT'));
SELECT pg_temp.assert_true('breaker: STUCK trade → breach',
       (SELECT breached FROM risk.fn_evaluate_circuit_breaker(1000, 1.0, 3, '2026-10-22') WHERE rule = 'STUCK_TRADES'));

-- =====================================================================
-- NEGATIVE TESTS (di-rollback agar tidak mencemari data skenario)
-- =====================================================================
\echo '--- negative tests: state machine & integrity guards ---'
BEGIN;
SELECT pg_temp.expect_error('insert trade langsung IN_FLIGHT',
  $q$INSERT INTO trd.trade_group (trade_id, run_id, strategy, canonical_asset, quote_asset, buy_instrument_id,
      sell_instrument_id, target_qty_base, exp_buy_vwap_px, exp_sell_vwap_px, exp_fee_quote, exp_net_quote,
      state, state_changed_at, created_at)
     VALUES (9000, 1, 'SPATIAL', 'BTC', 'USDT', 1, 2, 0.01, 60000, 60100, 1, 0.1, 'IN_FLIGHT', now(), now())$q$, 'AR002');

SELECT pg_temp.trade(9001, 1, 'SPATIAL', 'BTC', 1, 2, 0.01, 60000, 60100, 1, 0.1, '2026-10-22 10:00:00+00');
SELECT pg_temp.expect_error('transisi ilegal CREATED → COMPLETED',
  $q$UPDATE trd.trade_group SET state='COMPLETED', state_version=1, state_changed_at='2026-10-22 10:00:01+00' WHERE trade_id=9001$q$, 'AR002');
SELECT pg_temp.expect_error('version gap v0 → v2',
  $q$UPDATE trd.trade_group SET state='IN_FLIGHT', state_version=2, state_changed_at='2026-10-22 10:00:01+00' WHERE trade_id=9001$q$, 'AR003');
SELECT pg_temp.trans(9001, 'IN_FLIGHT', '2026-10-22 10:00:01+00');
-- replay identik (writer retry) → no-op
UPDATE trd.trade_group SET state='IN_FLIGHT', state_version=1, state_changed_at='2026-10-22 10:00:01+00' WHERE trade_id=9001;
SELECT pg_temp.assert_eq('replay idempoten tidak menambah log', (SELECT count(*) FROM trd.trade_state_log WHERE trade_id = 9001), 2);
SELECT pg_temp.expect_error('konflik di versi yang sama',
  $q$UPDATE trd.trade_group SET state='MISSED', state_version=1 WHERE trade_id=9001$q$, 'AR001');
SELECT pg_temp.expect_error('stale write (versi lebih lama)',
  $q$UPDATE trd.trade_group SET state='CREATED', state_version=0 WHERE trade_id=9001$q$, 'AR001');
SELECT pg_temp.expect_error('SPATIAL → POSITION_OPEN',
  $q$UPDATE trd.trade_group SET state='POSITION_OPEN', state_version=2, state_changed_at='2026-10-22 10:00:02+00' WHERE trade_id=9001$q$, 'AR002');
SELECT pg_temp.expect_error('waktu transisi mundur',
  $q$UPDATE trd.trade_group SET state='MISSED', state_version=2, state_changed_at='2026-10-22 09:59:59+00' WHERE trade_id=9001$q$, 'AR004');

SELECT pg_temp.trade(9002, 1, 'FUNDING_PERP_PERP', 'PEPE', 5, 4, 1000000, 0.000012, 0.000012006, 1, 0.1, '2026-10-22 10:00:00+00');
SELECT pg_temp.trans(9002, 'IN_FLIGHT', '2026-10-22 10:00:01+00');
SELECT pg_temp.expect_error('FUNDING → COMPLETED',
  $q$UPDATE trd.trade_group SET state='COMPLETED', state_version=2, state_changed_at='2026-10-22 10:00:02+00' WHERE trade_id=9002$q$, 'AR002');

SELECT pg_temp.expect_error('order: ubah kolom identitas',
  $q$UPDATE trd.order_leg SET side='SELL' WHERE order_id=10011$q$, 'AR005');
SELECT pg_temp.expect_error('fill: side beda dengan order',
  $q$INSERT INTO trd.fill (order_id, venue_trade_id, side, px, qty_venue, fee_amount, fee_asset, fee_quote, is_maker, venue_ts, received_at)
     VALUES (10011, 'bad-1', 'SELL', 60000, 0.001, 0, 'USDT', 0, false, now(), now())$q$, 'AR006');
SELECT pg_temp.expect_error('fill: append-only',
  $q$UPDATE trd.fill SET px = 1 WHERE venue_trade_id = '1001-1'$q$, 'AR006');
SELECT pg_temp.expect_error('order: role ENTRY_BUY dengan side SELL',
  $q$INSERT INTO trd.order_leg (order_id, trade_id, leg_role, instrument_id, side, order_type, time_in_force,
      client_order_id, limit_px, req_qty_venue, status, decided_at)
     VALUES (99999, 1001, 'ENTRY_BUY', 1, 'SELL', 'LIMIT', 'IOC', 'arb-99999', 60000, 0.01, 'SENT', now())$q$, '23514');
SELECT pg_temp.expect_error('order: cum_filled > req_qty',
  $q$UPDATE trd.order_leg SET cum_filled_qty_venue = 0.02 WHERE order_id = 10012$q$, '23514');
ROLLBACK;

-- =====================================================================
-- Snapshot hasil untuk inspeksi manual
-- =====================================================================
\echo ''
\echo '=== rpt.v_daily_pnl ==='
SELECT day_utc, mode, strategy, n_closed, n_missed, n_legged, n_win, n_loss, win_rate,
       round(realized_pnl_quote, 6) AS realized, round(fees_quote, 6) AS fees,
       round(funding_quote, 6) AS funding, capture_ratio_filled AS cap_filled, capture_ratio_all AS cap_all
FROM rpt.v_daily_pnl ORDER BY mode, strategy;

\echo '=== rpt.v_trade_ledger (ringkas) ==='
SELECT trade_id, mode, strategy, state, round(exp_net_quote, 6) AS expected,
       round(realized_pnl_quote, 7) AS realized, round(est_pnl_quote, 7) AS est,
       round(net_base_delta, 8) AS residual_base, is_flat, had_residual, time_to_flat_ms
FROM rpt.v_trade_ledger ORDER BY trade_id;

\echo '=== rpt.v_execution_quality ==='
SELECT mode, exchange_code, market_type, n_orders, avg_fill_ratio, reject_rate,
       round(p50_ack_ms::numeric, 1) AS p50_ms, round(p99_ack_ms::numeric, 1) AS p99_ms,
       avg_entry_slippage_bps, n_fill_mismatch
FROM rpt.v_execution_quality ORDER BY mode, exchange_code, market_type;

\echo '=== rpt.v_funding_positions ==='
SELECT trade_id, state, round(hold_hours, 4) AS hold_h, round(entry_notional_quote, 2) AS notional,
       n_funding, funding_quote, round(fees_total_quote, 6) AS fees,
       round(funding_yield_annualized * 100, 3) AS funding_apr_pct,
       round(net_yield_annualized * 100, 3) AS net_apr_pct, funding_covered_fees
FROM rpt.v_funding_positions;

\echo '=== risk.fn_evaluate_circuit_breaker(1000 USDT, 1%, 3) ==='
SELECT * FROM risk.fn_evaluate_circuit_breaker(1000, 1.0, 3, '2026-10-22');

\echo 'ALL TESTS PASSED'
