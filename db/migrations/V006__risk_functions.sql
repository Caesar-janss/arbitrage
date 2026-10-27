-- =====================================================================
-- V006 — Risk functions (PL/pgSQL)
-- ---------------------------------------------------------------------
-- Circuit breaker OTORITATIF berjalan di memori engine (dengan harga live).
-- Fungsi di sini dipakai untuk:
--   (1) REHYDRATION saat startup — loss streak & drawdown harian tidak boleh
--       ter-reset hanya karena proses restart,
--   (2) audit / laporan harian,
--   (3) operasi latch/clear halt yang konsisten dari engine maupun operator.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- Loss streak berjalan (dari trade terbaru ke belakang, berhenti di trade
-- non-rugi pertama). MISSED tidak dihitung (bukan kerugian, tidak memutus).
-- Streak di-reset oleh operator yang meng-clear halt CONSECUTIVE_LOSS:
-- hanya trade yang closed SETELAH clear terakhir yang dihitung. Tanpa ini,
-- breaker langsung trip lagi saat engine dinyalakan ulang.
-- ---------------------------------------------------------------------
CREATE FUNCTION risk.fn_consecutive_losses(
    p_mode     text DEFAULT 'LIVE',
    p_strategy text DEFAULT NULL)
RETURNS integer
LANGUAGE sql STABLE AS $$
    WITH reset AS (
        SELECT COALESCE(max(cleared_at), '-infinity'::timestamptz) AS since
        FROM risk.halt_state
        WHERE reason_code = 'CONSECUTIVE_LOSS' AND cleared_at IS NOT NULL
    ), t AS (
        SELECT l.closed_at, l.trade_id, l.realized_pnl_quote AS pnl
        FROM rpt.v_trade_ledger l, reset
        WHERE l.mode = p_mode
          AND (p_strategy IS NULL OR l.strategy = p_strategy)
          AND l.state IN ('COMPLETED','REPAIRED','CLOSED','MANUAL_RESOLVED')
          AND l.realized_pnl_quote IS NOT NULL
          AND l.closed_at > reset.since
    ), s AS (
        SELECT pnl,
               sum(CASE WHEN pnl >= 0 THEN 1 ELSE 0 END)
                   OVER (ORDER BY closed_at DESC, trade_id DESC ROWS UNBOUNDED PRECEDING) AS non_losses_seen
        FROM t
    )
    SELECT count(*)::integer FROM s WHERE non_losses_seen = 0
$$;

-- ---------------------------------------------------------------------
-- PnL hari berjalan untuk drawdown. Hari ditentukan p_tz (default UTC;
-- pakai 'Asia/Jakarta' bila batas harian ingin WIB).
--   realized_closed : trade terminal & flat yang closed hari itu
--   open_estimate   : estimasi trade terbuka/residual yang dibuat hari itu
--                     (MTM kasar: harga fill terakhir; engine pakai harga live)
-- ---------------------------------------------------------------------
CREATE FUNCTION risk.fn_daily_pnl(
    p_day  date,
    p_tz   text DEFAULT 'UTC',
    p_mode text DEFAULT 'LIVE')
RETURNS TABLE (realized_closed numeric, open_estimate numeric, total numeric, n_trades integer)
LANGUAGE sql STABLE AS $$
    WITH b AS (
        SELECT (p_day::timestamp AT TIME ZONE p_tz)       AS t0,
               ((p_day + 1)::timestamp AT TIME ZONE p_tz) AS t1
    ), x AS (
        SELECT
          COALESCE(sum(l.realized_pnl_quote) FILTER (
              WHERE l.is_terminal AND l.closed_at >= b.t0 AND l.closed_at < b.t1), 0) AS realized_closed,
          COALESCE(sum(l.est_pnl_quote) FILTER (
              WHERE (NOT l.is_terminal OR NOT l.is_flat)
                AND l.created_at >= b.t0 AND l.created_at < b.t1), 0)                 AS open_estimate,
          count(*) FILTER (
              WHERE (l.is_terminal AND l.closed_at >= b.t0 AND l.closed_at < b.t1)
                 OR ((NOT l.is_terminal OR NOT l.is_flat) AND l.created_at >= b.t0 AND l.created_at < b.t1)
          )::integer                                                                  AS n_trades
        FROM rpt.v_trade_ledger l, b
        WHERE l.mode = p_mode
    )
    SELECT realized_closed, open_estimate, realized_closed + open_estimate, n_trades FROM x
$$;

-- ---------------------------------------------------------------------
-- Evaluasi semua aturan breaker sekaligus (dipanggil saat startup & EOD).
-- Fungsi murni: TIDAK menulis halt. Engine yang memutuskan & memanggil latch.
-- ---------------------------------------------------------------------
CREATE FUNCTION risk.fn_evaluate_circuit_breaker(
    p_working_capital_quote numeric,
    p_max_daily_dd_pct      numeric DEFAULT 1.0,
    p_max_consec_losses     integer DEFAULT 3,
    p_day                   date    DEFAULT (now() AT TIME ZONE 'UTC')::date,
    p_tz                    text    DEFAULT 'UTC',
    p_mode                  text    DEFAULT 'LIVE')
RETURNS TABLE (rule text, current_value numeric, limit_value numeric, breached boolean)
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_total  numeric;
    v_streak integer;
    v_stuck  integer;
BEGIN
    IF p_working_capital_quote IS NULL OR p_working_capital_quote <= 0 THEN
        RAISE EXCEPTION 'working capital harus > 0';
    END IF;

    SELECT d.total INTO v_total FROM risk.fn_daily_pnl(p_day, p_tz, p_mode) d;
    v_streak := risk.fn_consecutive_losses(p_mode);
    SELECT count(*) INTO v_stuck
    FROM trd.trade_group tg JOIN trd.engine_run r USING (run_id)
    WHERE tg.state = 'STUCK' AND r.mode = p_mode;

    rule := 'DAILY_DRAWDOWN_PCT';
    current_value := round(greatest(-v_total, 0) / p_working_capital_quote * 100, 6);
    limit_value := p_max_daily_dd_pct;
    breached := current_value >= p_max_daily_dd_pct;
    RETURN NEXT;

    rule := 'CONSECUTIVE_LOSSES';
    current_value := v_streak;
    limit_value := p_max_consec_losses;
    breached := v_streak >= p_max_consec_losses;
    RETURN NEXT;

    rule := 'STUCK_TRADES';
    current_value := v_stuck;
    limit_value := 0;
    breached := v_stuck > 0;
    RETURN NEXT;
END
$$;

-- ---------------------------------------------------------------------
-- Latch halt (idempoten). Jika sudah ada halt aktif di scope yang sama,
-- level hanya boleh NAIK (NO_NEW_ENTRIES -> FLATTEN_AND_STOP), tidak turun.
-- ---------------------------------------------------------------------
CREATE FUNCTION risk.fn_latch_halt(
    p_scope          text,
    p_scope_key      text,
    p_level          text,
    p_reason_code    text,
    p_reason         text        DEFAULT NULL,
    p_auto_resume_at timestamptz DEFAULT NULL,
    p_at             timestamptz DEFAULT now())
RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE
    v_id    bigint;
    v_level text;
BEGIN
    SELECT halt_id, level INTO v_id, v_level
    FROM risk.halt_state
    WHERE scope = p_scope AND scope_key = p_scope_key AND cleared_at IS NULL
    FOR UPDATE;

    IF FOUND THEN
        IF v_level = 'NO_NEW_ENTRIES' AND p_level = 'FLATTEN_AND_STOP' THEN
            UPDATE risk.halt_state
               SET level = p_level, reason_code = p_reason_code, reason = p_reason, auto_resume_at = NULL
             WHERE halt_id = v_id;
        END IF;
        RETURN v_id;
    END IF;

    INSERT INTO risk.halt_state (scope, scope_key, level, reason_code, reason, halted_at, auto_resume_at)
    VALUES (p_scope, p_scope_key, p_level, p_reason_code, p_reason, p_at, p_auto_resume_at)
    RETURNING halt_id INTO v_id;
    RETURN v_id;
END
$$;

CREATE FUNCTION risk.fn_clear_halt(
    p_halt_id bigint,
    p_by      text,
    p_at      timestamptz DEFAULT now())
RETURNS boolean
LANGUAGE plpgsql AS $$
BEGIN
    IF p_by IS NULL OR length(trim(p_by)) = 0 THEN
        RAISE EXCEPTION 'cleared_by wajib diisi (audit)';
    END IF;
    UPDATE risk.halt_state SET cleared_at = p_at, cleared_by = p_by
     WHERE halt_id = p_halt_id AND cleared_at IS NULL;
    RETURN FOUND;
END
$$;

-- Halt yang berlaku sekarang (dibaca engine saat startup).
CREATE VIEW risk.v_active_halts AS
SELECT halt_id, scope, scope_key, level, reason_code, reason, halted_at, auto_resume_at,
       (auto_resume_at IS NULL) AS is_latched
FROM risk.halt_state
WHERE cleared_at IS NULL
  AND (auto_resume_at IS NULL OR auto_resume_at > now());

COMMIT;
