-- =====================================================================
-- V005 — Analytics views (rpt.*)
-- ---------------------------------------------------------------------
-- Model PnL: CASH-FLOW + MARK-TO-MARKET, satu rumus untuk semua strategi.
--
--   cash_flow  = Σ notional SELL − Σ notional BUY
--              − fee yang dibayar dalam quote atau koin pihak ketiga (BNB/BGB)
--              + Σ funding payment
--   net_delta  = Σ qty_base BUY − Σ qty_base SELL − fee yang dibayar dalam aset base
--   est_pnl    = cash_flow + net_delta × ref_px
--
-- Fee dalam aset base TIDAK dikurangkan dari kas (karena sudah mengurangi
-- net_delta) → tidak ada double counting. Berlaku juga untuk linear perp:
-- PnL perp = qty × (exit − entry) ekuivalen dengan arus notional.
-- realized_pnl_quote hanya terisi jika trade terminal DAN flat (≤ dust).
-- ref_px = harga fill terakhir di trade tsb (estimasi; MTM live ada di engine).
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- Per order leg: fill, fee, latency, slippage vs ekspektasi
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_leg_exec AS
WITH f AS (
    SELECT order_id,
           sum(qty_venue)  AS filled_qty_venue,
           sum(qty_base)   AS filled_qty_base,
           sum(quote_qty)  AS filled_quote,
           sum(fee_quote)  AS fee_quote,
           count(*)        AS n_fills,
           bool_or(is_maker) AS any_maker
    FROM trd.fill
    GROUP BY order_id
)
SELECT ol.order_id,
       ol.trade_id,
       r.mode,
       tg.strategy,
       ol.leg_role,
       ol.instrument_id,
       i.exchange_code,
       i.market_type,
       i.venue_symbol,
       ol.side,
       ol.order_type,
       ol.time_in_force,
       ol.status,
       ol.reject_code,
       ol.limit_px,
       ol.req_qty_venue,
       COALESCE(f.filled_qty_venue, 0)                         AS filled_qty_venue,
       COALESCE(f.filled_qty_base, 0)                          AS filled_qty_base,
       COALESCE(f.filled_quote, 0)                             AS filled_quote,
       COALESCE(f.fee_quote, 0)                                AS fee_quote,
       COALESCE(f.n_fills, 0)                                  AS n_fills,
       COALESCE(f.filled_qty_venue, 0) / ol.req_qty_venue      AS fill_ratio,
       f.filled_quote / NULLIF(f.filled_qty_base, 0)           AS fill_vwap_px,      -- canonical
       -- slippage (bps, positif = lebih buruk dari ekspektasi) hanya untuk entry leg
       CASE ol.leg_role
         WHEN 'ENTRY_BUY'  THEN (f.filled_quote / NULLIF(f.filled_qty_base, 0) - tg.exp_buy_vwap_px)
                                / tg.exp_buy_vwap_px * 1e4
         WHEN 'ENTRY_SELL' THEN (tg.exp_sell_vwap_px - f.filled_quote / NULLIF(f.filled_qty_base, 0))
                                / tg.exp_sell_vwap_px * 1e4
       END                                                     AS slippage_bps,
       ol.cum_filled_qty_venue,
       (trd.fn_order_status_rank(ol.status) = 9
        AND ol.cum_filled_qty_venue <> COALESCE(f.filled_qty_venue, 0)) AS fill_mismatch,
       ol.status_conflicts,
       extract(epoch FROM ol.sent_at     - ol.decided_at) * 1000 AS decide_to_send_ms,
       extract(epoch FROM ol.acked_at    - ol.sent_at)    * 1000 AS send_to_ack_ms,
       extract(epoch FROM ol.terminal_at - ol.sent_at)    * 1000 AS send_to_terminal_ms
FROM trd.order_leg ol
JOIN trd.trade_group tg ON tg.trade_id = ol.trade_id
JOIN trd.engine_run  r  ON r.run_id    = tg.run_id
JOIN ref.instrument  i  ON i.instrument_id = ol.instrument_id
LEFT JOIN f ON f.order_id = ol.order_id;

-- ---------------------------------------------------------------------
-- Ledger per trade group — fondasi semua analitik PnL
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_trade_ledger AS
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
    LEFT JOIN fu ON fu.trade_id = tg.trade_id
    LEFT JOIN sl ON sl.trade_id = tg.trade_id
), calc AS (
    SELECT b.*,
           b.gross_cash_quote - b.fee_cash_quote + b.funding_quote          AS cash_flow_quote,
           b.net_base_delta * COALESCE(b.last_fill_px, 0)                   AS residual_quote,
           abs(b.net_base_delta * COALESCE(b.last_fill_px, 0))
               <= ref.fn_setting('dust_quote')                              AS is_flat
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
       c.n_fills, c.any_simulated
FROM calc c;

-- ---------------------------------------------------------------------
-- PnL harian (UTC, by closed_at)
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_daily_pnl AS
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
       -- capture_ratio_filled: seberapa akurat model edge saat order memang terisi
       -- capture_ratio_all   : termasuk peluang yang hilang (MISSED) = edge riil
       round(sum(l.realized_pnl_quote) FILTER (WHERE l.state <> 'MISSED')
             / NULLIF(sum(l.exp_net_quote) FILTER (WHERE l.state <> 'MISSED'), 0), 4) AS capture_ratio_filled,
       round(sum(l.realized_pnl_quote) / NULLIF(sum(l.exp_net_quote), 0), 4)   AS capture_ratio_all,
       round(avg(l.buy_slippage_bps), 3)                                       AS avg_buy_slippage_bps,
       round(avg(l.sell_slippage_bps), 3)                                      AS avg_sell_slippage_bps,
       min(l.realized_pnl_quote)                                               AS worst_trade_quote,
       count(*) FILTER (WHERE l.realized_pnl_quote IS NULL)                    AS n_unrealized_terminal
FROM rpt.v_trade_ledger l
WHERE l.is_terminal
GROUP BY 1, 2, 3;

-- ---------------------------------------------------------------------
-- Kualitas eksekusi per venue: latency distribution, fill ratio, reject, slippage
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_execution_quality AS
SELECT e.mode,
       e.exchange_code,
       e.market_type,
       count(*)                                                               AS n_orders,
       round(avg(e.fill_ratio), 4)                                            AS avg_fill_ratio,
       round(count(*) FILTER (WHERE e.status = 'REJECTED')::numeric / count(*), 4) AS reject_rate,
       round(count(*) FILTER (WHERE e.status = 'UNKNOWN')::numeric  / count(*), 4) AS unknown_rate,
       percentile_cont(0.50) WITHIN GROUP (ORDER BY e.send_to_ack_ms)         AS p50_ack_ms,
       percentile_cont(0.95) WITHIN GROUP (ORDER BY e.send_to_ack_ms)         AS p95_ack_ms,
       percentile_cont(0.99) WITHIN GROUP (ORDER BY e.send_to_ack_ms)         AS p99_ack_ms,
       max(e.send_to_ack_ms)                                                  AS max_ack_ms,
       round(avg(e.decide_to_send_ms)::numeric, 3)                            AS avg_decide_to_send_ms,
       round(avg(e.slippage_bps), 3)                                          AS avg_entry_slippage_bps,
       sum(e.fee_quote)                                                       AS fees_quote,
       count(*) FILTER (WHERE e.fill_mismatch)                                AS n_fill_mismatch
FROM rpt.v_leg_exec e
GROUP BY 1, 2, 3;

-- ---------------------------------------------------------------------
-- Funnel peluang: di mana edge "bocor"
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_opportunity_funnel AS
SELECT (o.detected_at AT TIME ZONE 'UTC')::date                                AS day_utc,
       r.mode,
       o.strategy,
       o.decision,
       count(*)                                                                AS n,
       round(avg(o.gross_edge_bps), 3)                                         AS avg_gross_edge_bps,
       round(avg(o.expected_net_quote), 6)                                     AS avg_expected_net_quote,
       -- ilusi likuiditas: edge di top-of-book dikurangi edge VWAP pada ukuran riil
       round(avg((o.sell_tob_px - o.buy_tob_px) / o.buy_tob_px * 1e4 - o.gross_edge_bps), 3)
                                                                               AS avg_liquidity_illusion_bps,
       round(avg((o.buy_levels + o.sell_levels) / 2.0), 2)                     AS avg_levels_walked,
       round(avg(greatest(o.buy_book_age_us, o.sell_book_age_us)) / 1000.0, 3) AS avg_worst_book_age_ms
FROM trd.opportunity o
JOIN trd.engine_run r ON r.run_id = o.run_id
GROUP BY 1, 2, 3, 4;

-- ---------------------------------------------------------------------
-- Insiden legging: biaya & kecepatan perbaikan vs SLO
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_legging_incidents AS
SELECT l.trade_id, l.mode, l.strategy, l.canonical_asset, l.state, l.repair_method,
       l.first_residual_at, l.flat_after_repair_at, l.time_to_flat_ms,
       (l.time_to_flat_ms IS NULL OR l.time_to_flat_ms > ref.fn_setting('repair_slo_ms')) AS slo_breached,
       l.exp_net_quote,
       l.realized_pnl_quote,
       l.est_pnl_quote,
       l.est_pnl_quote - l.exp_net_quote AS legging_cost_vs_expected_quote,
       l.residual_quote,
       l.had_stuck
FROM rpt.v_trade_ledger l
WHERE l.had_residual;

-- ---------------------------------------------------------------------
-- Posisi funding arb: yield realisasi (annualized terhadap notional)
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_funding_positions AS
SELECT l.trade_id, l.mode, l.strategy, l.canonical_asset, l.state,
       l.position_open_at,
       l.closed_at,
       extract(epoch FROM COALESCE(l.closed_at, now()) - l.position_open_at) / 3600 AS hold_hours,
       l.entry_buy_qty_base * l.entry_buy_vwap_px                              AS entry_notional_quote,
       l.n_funding,
       l.funding_quote,
       l.fees_total_quote,
       l.est_pnl_quote - l.funding_quote                                       AS trading_pnl_ex_funding_quote,
       l.est_pnl_quote,
       l.realized_pnl_quote,
       l.funding_quote / NULLIF(l.entry_buy_qty_base * l.entry_buy_vwap_px, 0)
           / NULLIF(extract(epoch FROM COALESCE(l.closed_at, now()) - l.position_open_at) / 3600, 0)
           * 8760                                                              AS funding_yield_annualized,
       l.realized_pnl_quote / NULLIF(l.entry_buy_qty_base * l.entry_buy_vwap_px, 0)
           / NULLIF(extract(epoch FROM l.closed_at - l.position_open_at) / 3600, 0)
           * 8760                                                              AS net_yield_annualized,
       (l.funding_quote >= l.fees_total_quote)                                 AS funding_covered_fees
FROM rpt.v_trade_ledger l
WHERE l.strategy LIKE 'FUNDING%';

-- ---------------------------------------------------------------------
-- Inventory terkini & skew antar venue (pemicu rebalancing)
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_inventory AS
WITH latest AS (
    SELECT DISTINCT ON (exchange_code, account_type, asset_code, (source = 'PAPER'))
           exchange_code, account_type, asset_code, captured_at, free, locked, source,
           (source = 'PAPER') AS is_paper
    FROM acct.balance_snapshot
    ORDER BY exchange_code, account_type, asset_code, (source = 'PAPER'), captured_at DESC
)
SELECT exchange_code, account_type, asset_code, is_paper, source, captured_at,
       free, locked, free + locked AS total,
       round((free + locked) / NULLIF(sum(free + locked) OVER (PARTITION BY asset_code, is_paper), 0), 4)
           AS share_of_asset
FROM latest;

-- ---------------------------------------------------------------------
-- Eksposur terbuka & order hidup — dibaca StartupRecovery saat boot
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_open_exposure AS
SELECT l.trade_id, l.mode, l.strategy, l.state, l.canonical_asset,
       l.net_base_delta, l.residual_ref_px, l.residual_quote, l.is_flat,
       l.created_at,
       (SELECT count(*) FROM trd.order_leg ol
        WHERE ol.trade_id = l.trade_id AND trd.fn_order_status_rank(ol.status) < 9) AS live_orders
FROM rpt.v_trade_ledger l
WHERE NOT l.is_terminal OR NOT l.is_flat;

-- ---------------------------------------------------------------------
-- Integritas data order: fill hilang / laporan terminal bertentangan
-- ---------------------------------------------------------------------
CREATE VIEW rpt.v_order_integrity AS
SELECT e.order_id, e.trade_id, e.mode, e.exchange_code, e.venue_symbol, e.status,
       e.cum_filled_qty_venue, e.filled_qty_venue,
       e.cum_filled_qty_venue - e.filled_qty_venue AS missing_qty_venue,
       e.status_conflicts,
       CASE WHEN e.fill_mismatch AND e.status_conflicts > 0 THEN 'MISSING_FILL+STATUS_CONFLICT'
            WHEN e.fill_mismatch                         THEN 'MISSING_FILL'
            ELSE 'STATUS_CONFLICT' END              AS issue
FROM rpt.v_leg_exec e
WHERE e.fill_mismatch OR e.status_conflicts > 0;

COMMIT;
