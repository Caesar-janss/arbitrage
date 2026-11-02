-- =====================================================================
-- V008 — Perbaikan dari review Tahap 1 ronde 2 (review ChatGPT PR #2,
-- docs/review/FINDINGS_LOG.md). Migrasi yang sudah dirilis (V001…V007)
-- tidak diubah.
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- C-08: hedge SPATIAL yang di-unwind SEBAGIAN (spot dan perp sama-sama
-- dikurangi) kembali EXITING → POSITION_OPEN; sisanya tetap di-hedge dan
-- dimiliki trade. Aturan entry tidak berubah: SPATIAL tidak pernah
-- IN_FLIGHT → POSITION_OPEN, dan POSITION_OPEN hanya dengan PERP_HEDGE.
-- Mirror TradeStateMachine.IsAllowedFor. Catatan: SQL menjaga label
-- repair_method; cek "perp benar-benar masih terbuka" ada di C#
-- (TradeGroup → NotHedged), bukan di trigger ini.
-- ---------------------------------------------------------------------
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
       AND (OLD.state NOT IN ('REPAIRING', 'EXITING') OR NEW.repair_method IS DISTINCT FROM 'PERP_HEDGE') THEN
        RAISE EXCEPTION 'trade %: SPATIAL hanya masuk POSITION_OPEN dari REPAIRING/EXITING dengan PERP_HEDGE (dari %, metode %)',
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

COMMIT;
