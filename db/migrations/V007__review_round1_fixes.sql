-- =====================================================================
-- V007 — Perbaikan dari review Tahap 1 ronde 1 (docs/review/FINDINGS_LOG.md)
-- ---------------------------------------------------------------------
-- Satu migrasi, beberapa temuan; setiap bagian diberi label temuannya.
-- Kontrak C# ↔ SQL tetap dites: TradeStateMachineTests membaca SEMUA
-- migrasi (V002 + V007) untuk tabel transisi.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- R-02: residual SPATIAL yang di-hedge di perp punya lifecycle sendiri:
--   REPAIRING → POSITION_OPEN (spot + perp terbuka) → EXITING → CLOSED.
-- Hedge perp tidak pernah boleh berakhir REPAIRED (terminal) selama kaki
-- spot & perp masih terbuka; v_trade_ledger/v_open_exposure menghitung
-- posisi perp per instrumen (bukan hanya net delta).
-- ---------------------------------------------------------------------
UPDATE trd.trade_state SET description = 'FUNDING: posisi delta-neutral ditahan; SPATIAL: residual di-hedge di perp (spot + perp terbuka)'
 WHERE state = 'POSITION_OPEN';
UPDATE trd.trade_state SET description = 'Order exit berjalan (FUNDING, atau unwind hedge SPATIAL)'
 WHERE state = 'EXITING';
UPDATE trd.trade_state SET description = 'Posisi (FUNDING / hedge SPATIAL) ditutup penuh'
 WHERE state = 'CLOSED';

CREATE OR REPLACE FUNCTION trd.trg_trade_state_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_terminal boolean;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.state <> 'CREATED' OR NEW.state_version <> 0 THEN
            RAISE EXCEPTION 'trade %: insert harus CREATED/v0 (didapat %/v%)',
                NEW.trade_id, NEW.state, NEW.state_version
                USING ERRCODE = 'AR002';
        END IF;
        RETURN NEW;
    END IF;

    -- UPDATE --------------------------------------------------------
    IF NEW.state_version = OLD.state_version THEN
        IF NEW.state IS DISTINCT FROM OLD.state THEN
            RAISE EXCEPTION 'trade %: konflik di v% (% vs %)',
                NEW.trade_id, NEW.state_version, OLD.state, NEW.state
                USING ERRCODE = 'AR001';
        END IF;
        RETURN NEW;                      -- update kolom non-state, atau replay idempoten
    ELSIF NEW.state_version < OLD.state_version THEN
        RAISE EXCEPTION 'trade %: stale write v% < v%',
            NEW.trade_id, NEW.state_version, OLD.state_version
            USING ERRCODE = 'AR001';
    ELSIF NEW.state_version > OLD.state_version + 1 THEN
        RAISE EXCEPTION 'trade %: version gap v% -> v%',
            NEW.trade_id, OLD.state_version, NEW.state_version
            USING ERRCODE = 'AR003';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM trd.trade_state_transition t
                   WHERE t.from_state = OLD.state AND t.to_state = NEW.state) THEN
        RAISE EXCEPTION 'trade %: transisi ilegal % -> %', NEW.trade_id, OLD.state, NEW.state
            USING ERRCODE = 'AR002';
    END IF;

    -- Aturan strategi (mirror TradeStateMachine.IsAllowedFor + TradeGroup.TransitionTo)
    IF NEW.strategy = 'SPATIAL' AND NEW.state = 'POSITION_OPEN'
       AND (OLD.state <> 'REPAIRING' OR NEW.repair_method IS DISTINCT FROM 'PERP_HEDGE') THEN
        RAISE EXCEPTION 'trade %: SPATIAL hanya masuk POSITION_OPEN dari REPAIRING dengan PERP_HEDGE (dari %, metode %)',
            NEW.trade_id, OLD.state, NEW.repair_method
            USING ERRCODE = 'AR002';
    END IF;
    IF NEW.strategy = 'SPATIAL' AND NEW.state = 'REPAIRED' AND NEW.repair_method = 'PERP_HEDGE' THEN
        RAISE EXCEPTION 'trade %: hedge perp tidak boleh berakhir REPAIRED (spot + perp masih terbuka; lewat POSITION_OPEN → EXITING → CLOSED)',
            NEW.trade_id
            USING ERRCODE = 'AR002';
    END IF;
    IF NEW.strategy <> 'SPATIAL' AND NEW.state = 'COMPLETED' THEN
        RAISE EXCEPTION 'trade %: strategi funding selesai via POSITION_OPEN/CLOSED, bukan COMPLETED', NEW.trade_id
            USING ERRCODE = 'AR002';
    END IF;

    IF NEW.state_changed_at < OLD.state_changed_at THEN
        RAISE EXCEPTION 'trade %: waktu transisi mundur (% < %)',
            NEW.trade_id, NEW.state_changed_at, OLD.state_changed_at
            USING ERRCODE = 'AR004';
    END IF;

    SELECT is_terminal INTO v_terminal FROM trd.trade_state WHERE state = NEW.state;
    IF v_terminal THEN
        NEW.closed_at := COALESCE(NEW.closed_at, NEW.state_changed_at);
    ELSE
        NEW.closed_at := NULL;          -- STUCK -> REPAIRING dsb. tidak boleh punya closed_at
    END IF;
    RETURN NEW;
END
$$;

-- ---------------------------------------------------------------------
-- R-14: SATU definisi flat, milik engine. TradeGroup.AcceptResidualAsDust
-- menulis residual yang di-write-off (di bawah minimum venue / ≤ DustCanon)
-- ke dust_accepted_base. Flat ⇔ tidak ada perp terbuka DAN
-- net_base_delta = dust_accepted_base (eksak; NULL = 0). ref.setting
-- dust_quote tidak lagi menentukan is_flat: hanya ambang ALERT nilai dust.
-- ---------------------------------------------------------------------
ALTER TABLE trd.trade_group ADD COLUMN dust_accepted_base ref.amt;   -- signed canonical base, NULL = tidak ada
UPDATE ref.setting
   SET note = 'Ambang ALERT nilai dust yang diterima engine (v_trade_ledger.dust_over_limit); tidak lagi dipakai untuk is_flat (R-14)'
 WHERE key = 'dust_quote';

-- ---------------------------------------------------------------------
-- R-16: ekspektasi disimpan per komponen. exp_net_quote (gate entry) sudah
-- dikurangi buffer slippage & rebalance amortisasi yang TIDAK PERNAH muncul di
-- fill → capture ratio terinflasi (1.29 untuk trade sempurna). Basis yang
-- sebanding dengan realized = exp_cash = exp_net + buffer + rebalance
-- (= EdgeBreakdown.ExpectedCashQuote). Biaya rebalance riil dibandingkan
-- terpisah (rpt.v_rebalance_cost_daily vs acct.transfer).
-- ---------------------------------------------------------------------
ALTER TABLE trd.trade_group
    ADD COLUMN exp_slippage_buffer_quote ref.amt NOT NULL DEFAULT 0,
    ADD COLUMN exp_rebalance_quote       ref.amt NOT NULL DEFAULT 0;

-- ---------------------------------------------------------------------
-- Ledger: is_flat = (net delta = dust yang diterima engine) DAN tidak ada
-- posisi perp terbuka (R-02, R-14). Kolom baru di AKHIR (syarat CREATE OR
-- REPLACE VIEW).
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW rpt.v_trade_ledger AS
WITH fx AS (
    SELECT ol.trade_id,
           sum(CASE WHEN f.side = 'SELL' THEN f.quote_qty ELSE -f.quote_qty END)          AS gross_cash_quote,
           sum(CASE WHEN f.fee_asset = tg.quote_asset     THEN f.fee_amount
                    WHEN f.fee_asset = tg.canonical_asset THEN 0
                    ELSE f.fee_quote END)                                                  AS fee_cash_quote,
           sum(CASE WHEN f.side = 'BUY' THEN f.qty_base ELSE -f.qty_base END)
             - sum(CASE WHEN f.fee_asset = tg.canonical_asset THEN f.fee_amount ELSE 0 END) AS net_base_delta,
           sum(f.fee_quote)                                                               AS fees_total_quote,
           sum(f.qty_base)  FILTER (WHERE ol.leg_role = 'ENTRY_BUY')                      AS entry_buy_qty_base,
           sum(f.qty_base)  FILTER (WHERE ol.leg_role = 'ENTRY_SELL')                     AS entry_sell_qty_base,
           sum(f.quote_qty) FILTER (WHERE ol.leg_role = 'ENTRY_BUY')
             / NULLIF(sum(f.qty_base) FILTER (WHERE ol.leg_role = 'ENTRY_BUY'), 0)        AS entry_buy_vwap_px,
           sum(f.quote_qty) FILTER (WHERE ol.leg_role = 'ENTRY_SELL')
             / NULLIF(sum(f.qty_base) FILTER (WHERE ol.leg_role = 'ENTRY_SELL'), 0)       AS entry_sell_vwap_px,
           (array_agg(f.quote_qty / f.qty_base ORDER BY f.venue_ts DESC, f.fill_id DESC))[1] AS last_fill_px,
           count(*)                                                                       AS n_fills,
           bool_or(f.is_simulated)                                                        AS any_simulated
    FROM trd.fill f
    JOIN trd.order_leg   ol ON ol.order_id = f.order_id
    JOIN trd.trade_group tg ON tg.trade_id = ol.trade_id
    GROUP BY ol.trade_id
), pp AS (
    -- posisi perp per instrumen (entry leg funding ATAU hedge spatial), lalu dijumlah |posisi|
    SELECT x.trade_id, sum(abs(x.net_qty_base)) AS open_perp_qty_base
    FROM (SELECT ol.trade_id, f.instrument_id,
                 sum(CASE WHEN f.side = 'BUY' THEN f.qty_base ELSE -f.qty_base END) AS net_qty_base
          FROM trd.fill f
          JOIN trd.order_leg  ol ON ol.order_id = f.order_id
          JOIN ref.instrument i  ON i.instrument_id = f.instrument_id
          WHERE i.market_type = 'PERP_LINEAR'
          GROUP BY ol.trade_id, f.instrument_id) x
    GROUP BY x.trade_id
), fu AS (
    SELECT trade_id, sum(amount_quote) AS funding_quote, count(*) AS n_funding
    FROM trd.funding_payment
    WHERE trade_id IS NOT NULL
    GROUP BY trade_id
), sl AS (
    SELECT trade_id,
           bool_or(to_state = 'RESIDUAL')                                       AS had_residual,
           bool_or(to_state = 'STUCK')                                          AS had_stuck,
           min(changed_at) FILTER (WHERE to_state = 'IN_FLIGHT')                AS in_flight_at,
           min(changed_at) FILTER (WHERE to_state = 'RESIDUAL')                 AS first_residual_at,
           max(changed_at) FILTER (WHERE from_state = 'REPAIRING'
                                     AND to_state IN ('REPAIRED','POSITION_OPEN','CLOSED')) AS flat_after_repair_at,
           min(changed_at) FILTER (WHERE to_state = 'POSITION_OPEN')            AS position_open_at
    FROM trd.trade_state_log
    GROUP BY trade_id
), base AS (
    SELECT tg.*,
           r.mode,
           ts.is_terminal,
           fx.entry_buy_qty_base, fx.entry_sell_qty_base,
           fx.entry_buy_vwap_px,  fx.entry_sell_vwap_px,
           COALESCE(fx.gross_cash_quote, 0)  AS gross_cash_quote,
           COALESCE(fx.fee_cash_quote, 0)    AS fee_cash_quote,
           COALESCE(fx.fees_total_quote, 0)  AS fees_total_quote,
           COALESCE(fu.funding_quote, 0)     AS funding_quote,
           COALESCE(fu.n_funding, 0)         AS n_funding,
           COALESCE(fx.net_base_delta, 0)    AS net_base_delta,
           COALESCE(pp.open_perp_qty_base, 0) AS open_perp_qty_base,
           fx.last_fill_px,
           COALESCE(fx.n_fills, 0)           AS n_fills,
           COALESCE(fx.any_simulated, false) AS any_simulated,
           COALESCE(sl.had_residual, false)  AS had_residual,
           COALESCE(sl.had_stuck, false)     AS had_stuck,
           sl.in_flight_at, sl.first_residual_at, sl.flat_after_repair_at, sl.position_open_at
    FROM trd.trade_group tg
    JOIN trd.engine_run  r  ON r.run_id = tg.run_id
    JOIN trd.trade_state ts ON ts.state = tg.state
    LEFT JOIN fx ON fx.trade_id = tg.trade_id
    LEFT JOIN pp ON pp.trade_id = tg.trade_id
    LEFT JOIN fu ON fu.trade_id = tg.trade_id
    LEFT JOIN sl ON sl.trade_id = tg.trade_id
), calc AS (
    SELECT b.*,
           b.gross_cash_quote - b.fee_cash_quote + b.funding_quote          AS cash_flow_quote,
           b.net_base_delta * COALESCE(b.last_fill_px, 0)                   AS residual_quote,
           b.net_base_delta = COALESCE(b.dust_accepted_base, 0)
             AND b.open_perp_qty_base = 0                                   AS is_flat,
           COALESCE(b.dust_accepted_base, 0) * COALESCE(b.last_fill_px, 0)  AS dust_accepted_quote,
           b.exp_net_quote + b.exp_slippage_buffer_quote + b.exp_rebalance_quote AS exp_cash_quote
    FROM base b
)
SELECT c.trade_id, c.run_id, c.mode, c.strategy, c.canonical_asset, c.quote_asset,
       c.state, c.is_terminal, c.repair_method,
       c.buy_instrument_id, c.sell_instrument_id, c.target_qty_base,
       c.created_at, c.in_flight_at, c.closed_at,
       -- ekspektasi saat keputusan
       c.exp_buy_vwap_px, c.exp_sell_vwap_px, c.exp_fee_quote, c.exp_net_quote,
       -- realisasi entry
       c.entry_buy_qty_base, c.entry_sell_qty_base,
       c.entry_buy_vwap_px,  c.entry_sell_vwap_px,
       (c.entry_buy_vwap_px  - c.exp_buy_vwap_px)  / c.exp_buy_vwap_px  * 1e4 AS buy_slippage_bps,
       (c.exp_sell_vwap_px   - c.entry_sell_vwap_px) / c.exp_sell_vwap_px * 1e4 AS sell_slippage_bps,
       -- arus kas & eksposur
       c.gross_cash_quote, c.fee_cash_quote, c.fees_total_quote, c.funding_quote, c.n_funding,
       c.cash_flow_quote,
       c.net_base_delta, c.last_fill_px AS residual_ref_px, c.residual_quote, c.is_flat,
       c.cash_flow_quote + c.residual_quote                                    AS est_pnl_quote,
       CASE WHEN c.is_terminal AND c.is_flat
            THEN c.cash_flow_quote + c.residual_quote END                      AS realized_pnl_quote,
       CASE WHEN c.is_terminal AND c.is_flat AND c.state <> 'MISSED'
            THEN c.exp_cash_quote - (c.cash_flow_quote + c.residual_quote) END AS shortfall_quote,  -- R-16: basis exp_cash
       -- legging
       c.had_residual, c.had_stuck, c.first_residual_at, c.flat_after_repair_at,
       CASE WHEN c.had_residual AND c.flat_after_repair_at IS NOT NULL
            THEN extract(epoch FROM c.flat_after_repair_at - c.first_residual_at) * 1000 END AS time_to_flat_ms,
       c.position_open_at,
       c.n_fills, c.any_simulated,
       -- v0.3 (kolom baru di akhir)
       c.open_perp_qty_base,
       c.dust_accepted_base,
       c.dust_accepted_quote,
       abs(c.dust_accepted_quote) > ref.fn_setting('dust_quote')              AS dust_over_limit,
       c.exp_slippage_buffer_quote,
       c.exp_rebalance_quote,
       c.exp_cash_quote,
       CASE WHEN c.is_terminal AND c.is_flat AND c.state <> 'MISSED'
            THEN round((c.cash_flow_quote + c.residual_quote) / NULLIF(c.exp_cash_quote, 0), 6) END AS capture_ratio
FROM calc c;

-- R-16: capture ratio harian pada basis yang sama (kolom lama, arti dikoreksi) + kolom baru di akhir
CREATE OR REPLACE VIEW rpt.v_daily_pnl AS
SELECT (l.closed_at AT TIME ZONE 'UTC')::date                                   AS day_utc,
       l.mode,
       l.strategy,
       count(*)                                                                AS n_closed,
       count(*) FILTER (WHERE l.state = 'MISSED')                              AS n_missed,
       count(*) FILTER (WHERE l.had_residual)                                  AS n_legged,
       count(*) FILTER (WHERE l.realized_pnl_quote > 0)                        AS n_win,
       count(*) FILTER (WHERE l.realized_pnl_quote < 0)                        AS n_loss,
       round(count(*) FILTER (WHERE l.realized_pnl_quote > 0)::numeric
             / NULLIF(count(*) FILTER (WHERE l.state <> 'MISSED'), 0), 4)      AS win_rate,
       sum(l.realized_pnl_quote)                                               AS realized_pnl_quote,
       sum(l.fees_total_quote)                                                 AS fees_quote,
       sum(l.funding_quote)                                                    AS funding_quote,
       sum(l.exp_net_quote) FILTER (WHERE l.state <> 'MISSED')                 AS expected_pnl_filled,
       sum(l.exp_net_quote)                                                    AS expected_pnl_all,
       round(sum(l.realized_pnl_quote) FILTER (WHERE l.state <> 'MISSED')
             / NULLIF(sum(l.exp_cash_quote) FILTER (WHERE l.state <> 'MISSED'), 0), 4) AS capture_ratio_filled,
       round(sum(l.realized_pnl_quote) / NULLIF(sum(l.exp_cash_quote), 0), 4)  AS capture_ratio_all,
       round(avg(l.buy_slippage_bps), 3)                                       AS avg_buy_slippage_bps,
       round(avg(l.sell_slippage_bps), 3)                                      AS avg_sell_slippage_bps,
       min(l.realized_pnl_quote)                                               AS worst_trade_quote,
       count(*) FILTER (WHERE l.realized_pnl_quote IS NULL)                    AS n_unrealized_terminal,
       -- v0.3
       sum(l.exp_cash_quote) FILTER (WHERE l.state <> 'MISSED')                AS expected_cash_filled,
       sum(l.exp_rebalance_quote) FILTER (WHERE l.state <> 'MISSED')           AS expected_rebalance_quote
FROM rpt.v_trade_ledger l
WHERE l.is_terminal
GROUP BY 1, 2, 3;

-- R-16: biaya rebalance — amortisasi yang diasumsikan trade vs fee transfer riil per hari (kalibrasi model)
CREATE VIEW rpt.v_rebalance_cost_daily AS
WITH e AS (
    SELECT (l.closed_at AT TIME ZONE 'UTC')::date AS day_utc, sum(l.exp_rebalance_quote) AS expected_quote
    FROM rpt.v_trade_ledger l
    WHERE l.is_terminal AND l.mode = 'LIVE' AND l.state <> 'MISSED'
    GROUP BY 1
), r AS (
    SELECT (t.completed_at AT TIME ZONE 'UTC')::date AS day_utc, sum(t.fee_quote) AS realized_quote, count(*) AS n_transfers
    FROM acct.transfer t
    WHERE t.status = 'COMPLETED'
    GROUP BY 1
)
SELECT COALESCE(e.day_utc, r.day_utc)          AS day_utc,
       COALESCE(e.expected_quote, 0)          AS expected_rebalance_quote,
       COALESCE(r.realized_quote, 0)          AS realized_transfer_fee_quote,
       COALESCE(r.n_transfers, 0)             AS n_transfers,
       COALESCE(r.realized_quote, 0) - COALESCE(e.expected_quote, 0) AS model_error_quote
FROM e FULL JOIN r ON r.day_utc = e.day_utc;

CREATE OR REPLACE VIEW rpt.v_open_exposure AS
SELECT l.trade_id, l.mode, l.strategy, l.state, l.canonical_asset,
       l.net_base_delta, l.residual_ref_px, l.residual_quote, l.is_flat,
       l.created_at,
       (SELECT count(*) FROM trd.order_leg ol
        WHERE ol.trade_id = l.trade_id AND trd.fn_order_status_rank(ol.status) < 9) AS live_orders,
       l.open_perp_qty_base
FROM rpt.v_trade_ledger l
WHERE NOT l.is_terminal OR NOT l.is_flat;

-- ---------------------------------------------------------------------
-- R-14: loss streak juga menghitung trade terminal yang TIDAK flat (PnL
-- tidak diketahui → dianggap rugi; v0.2 melewatinya karena realized NULL).
-- ---------------------------------------------------------------------
CREATE OR REPLACE FUNCTION risk.fn_consecutive_losses(
    p_mode     text DEFAULT 'LIVE',
    p_strategy text DEFAULT NULL)
RETURNS integer
LANGUAGE sql STABLE AS $$
    WITH reset AS (
        SELECT COALESCE(max(cleared_at), '-infinity'::timestamptz) AS since
        FROM risk.halt_state
        WHERE reason_code = 'CONSECUTIVE_LOSS' AND cleared_at IS NOT NULL
    ), t AS (
        SELECT l.closed_at, l.trade_id,
               (l.realized_pnl_quote IS NULL OR l.realized_pnl_quote < 0) AS is_loss
        FROM rpt.v_trade_ledger l, reset
        WHERE l.mode = p_mode
          AND (p_strategy IS NULL OR l.strategy = p_strategy)
          AND l.state IN ('COMPLETED','REPAIRED','CLOSED','MANUAL_RESOLVED')
          AND l.closed_at > reset.since
    ), s AS (
        SELECT is_loss,
               sum(CASE WHEN is_loss THEN 0 ELSE 1 END)
                   OVER (ORDER BY closed_at DESC, trade_id DESC ROWS UNBOUNDED PRECEDING) AS non_losses_seen
        FROM t
    )
    SELECT count(*)::integer FROM s WHERE non_losses_seen = 0
$$;

-- ---------------------------------------------------------------------
-- R-15: quote yang ditolak sebelum kedua kaki di-walk tidak punya VWAP /
-- worst px / level / edge. Kolom-kolom itu NULL untuk reject (SpatialQuote:
-- NaN → NULL) dan WAJIB hanya untuk EXECUTE. Tanpa ini satu baris reject
-- menggagalkan seluruh batch COPY (termasuk baris EXECUTE).
-- ---------------------------------------------------------------------
ALTER TABLE trd.opportunity
    ALTER COLUMN qty_base              DROP NOT NULL,
    ALTER COLUMN buy_vwap_px           DROP NOT NULL,
    ALTER COLUMN sell_vwap_px          DROP NOT NULL,
    ALTER COLUMN buy_worst_px          DROP NOT NULL,
    ALTER COLUMN sell_worst_px         DROP NOT NULL,
    ALTER COLUMN buy_levels            DROP NOT NULL,
    ALTER COLUMN sell_levels           DROP NOT NULL,
    ALTER COLUMN gross_edge_bps        DROP NOT NULL,
    ALTER COLUMN fee_cost_quote        DROP NOT NULL,
    ALTER COLUMN slippage_buffer_quote DROP NOT NULL,
    ALTER COLUMN rebalance_cost_quote  DROP NOT NULL,
    ALTER COLUMN expected_net_quote    DROP NOT NULL;
ALTER TABLE trd.opportunity ADD CONSTRAINT opportunity_execute_complete CHECK (
    decision <> 'EXECUTE' OR (
        qty_base IS NOT NULL AND buy_vwap_px IS NOT NULL AND sell_vwap_px IS NOT NULL
        AND buy_worst_px IS NOT NULL AND sell_worst_px IS NOT NULL
        AND buy_levels IS NOT NULL AND sell_levels IS NOT NULL
        AND gross_edge_bps IS NOT NULL AND fee_cost_quote IS NOT NULL
        AND slippage_buffer_quote IS NOT NULL AND rebalance_cost_quote IS NOT NULL
        AND expected_net_quote IS NOT NULL));

-- ---------------------------------------------------------------------
-- R-10: transisi yang dibutuhkan risk code saat posisi ditahan
-- (mirror TradeStateMachine.TransitionList; dites oleh TradeStateMachineTests)
-- ---------------------------------------------------------------------
INSERT INTO trd.trade_state_transition (from_state, to_state) VALUES
  ('POSITION_OPEN', 'RESIDUAL'),        -- satu kaki dilikuidasi / ADL saat menahan posisi
  ('POSITION_OPEN', 'STUCK'),           -- venue tak terjangkau / rekonsiliasi gagal saat menahan posisi
  ('EXITING',       'POSITION_OPEN');   -- partial reduce (MarginGuard ReducePair) selesai, sisa tetap terbuka

COMMIT;
