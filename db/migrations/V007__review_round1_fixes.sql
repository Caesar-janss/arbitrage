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
-- Ledger: is_flat = delta ≤ dust DAN tidak ada posisi perp terbuka (R-02).
-- Kolom baru ditambahkan di AKHIR (syarat CREATE OR REPLACE VIEW).
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
           abs(b.net_base_delta * COALESCE(b.last_fill_px, 0)) <= ref.fn_setting('dust_quote')
             AND b.open_perp_qty_base = 0                                   AS is_flat
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
            THEN c.exp_net_quote - (c.cash_flow_quote + c.residual_quote) END  AS shortfall_quote,
       -- legging
       c.had_residual, c.had_stuck, c.first_residual_at, c.flat_after_repair_at,
       CASE WHEN c.had_residual AND c.flat_after_repair_at IS NOT NULL
            THEN extract(epoch FROM c.flat_after_repair_at - c.first_residual_at) * 1000 END AS time_to_flat_ms,
       c.position_open_at,
       c.n_fills, c.any_simulated,
       -- v0.3 (kolom baru di akhir)
       c.open_perp_qty_base
FROM calc c;

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
-- R-10: transisi yang dibutuhkan risk code saat posisi ditahan
-- (mirror TradeStateMachine.TransitionList; dites oleh TradeStateMachineTests)
-- ---------------------------------------------------------------------
INSERT INTO trd.trade_state_transition (from_state, to_state) VALUES
  ('POSITION_OPEN', 'RESIDUAL'),        -- satu kaki dilikuidasi / ADL saat menahan posisi
  ('POSITION_OPEN', 'STUCK'),           -- venue tak terjangkau / rekonsiliasi gagal saat menahan posisi
  ('EXITING',       'POSITION_OPEN');   -- partial reduce (MarginGuard ReducePair) selesai, sisa tetap terbuka

COMMIT;
