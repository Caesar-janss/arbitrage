-- =====================================================================
-- V004 — Integrity triggers (PL/pgSQL) & partition management
-- ---------------------------------------------------------------------
-- DB adalah LAPIS PERTAHANAN KEDUA. State machine otoritatif ada di engine
-- (memori). Trigger di sini menangkap bug engine / writer yang menulis
-- tidak berurutan, dan menghasilkan audit trail untuk RCA legging.
--
-- Custom SQLSTATE (ditangani AsyncPersistenceWriter):
--   AR001  stale / conflicting write      -> idempotent duplicate: drop + metric
--   AR002  illegal state transition       -> BUG engine: alert
--   AR003  version gap (event hilang)     -> BUG writer: alert + replay
--   AR004  waktu mundur pada transisi     -> alert
--   AR005  kolom identitas order diubah   -> BUG: alert
--   AR006  fill tidak cocok dgn order     -> BUG adapter: alert
-- =====================================================================
BEGIN;

-- ---------------------------------------------------------------------
-- Trade state guard
-- ---------------------------------------------------------------------
CREATE FUNCTION trd.trg_trade_state_guard() RETURNS trigger
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

    IF NEW.strategy = 'SPATIAL' AND NEW.state IN ('POSITION_OPEN','EXITING','CLOSED') THEN
        RAISE EXCEPTION 'trade %: SPATIAL tidak boleh masuk %', NEW.trade_id, NEW.state
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

CREATE TRIGGER trade_state_guard
BEFORE INSERT OR UPDATE ON trd.trade_group
FOR EACH ROW EXECUTE FUNCTION trd.trg_trade_state_guard();

CREATE FUNCTION trd.trg_trade_state_log() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        INSERT INTO trd.trade_state_log (trade_id, seq, from_state, to_state, changed_at, reason)
        VALUES (NEW.trade_id, 0, NULL, NEW.state, NEW.state_changed_at, NEW.state_reason);
    ELSIF NEW.state_version <> OLD.state_version THEN
        INSERT INTO trd.trade_state_log (trade_id, seq, from_state, to_state, changed_at, reason)
        VALUES (NEW.trade_id, NEW.state_version, OLD.state, NEW.state, NEW.state_changed_at, NEW.state_reason);
    END IF;
    RETURN NULL;
END
$$;

CREATE TRIGGER trade_state_log
AFTER INSERT OR UPDATE ON trd.trade_group
FOR EACH ROW EXECUTE FUNCTION trd.trg_trade_state_log();

-- ---------------------------------------------------------------------
-- Order leg: merge monoton (aman untuk UPSERT out-of-order)
-- ---------------------------------------------------------------------
CREATE FUNCTION trd.fn_order_status_rank(p_status text) RETURNS smallint
LANGUAGE sql IMMUTABLE AS $$
    SELECT CASE p_status
             WHEN 'PENDING_SEND'     THEN 0
             WHEN 'SENT'             THEN 1
             WHEN 'UNKNOWN'          THEN 2    -- timeout tanpa respons: harus di-query by clientOrderId
             WHEN 'ACKED'            THEN 3
             WHEN 'PARTIALLY_FILLED' THEN 4
             ELSE 9                            -- FILLED / CANCELED / REJECTED / EXPIRED (terminal)
           END::smallint
$$;

CREATE FUNCTION trd.trg_order_leg_merge() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    r_old smallint;
    r_new smallint;
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF trd.fn_order_status_rank(NEW.status) = 9 THEN
            NEW.terminal_at := COALESCE(NEW.terminal_at, NEW.acked_at, now());
        END IF;
        NEW.updated_at := now();
        RETURN NEW;
    END IF;

    IF NEW.trade_id      <> OLD.trade_id
    OR NEW.instrument_id <> OLD.instrument_id
    OR NEW.side          <> OLD.side
    OR NEW.client_order_id <> OLD.client_order_id
    OR NEW.req_qty_venue <> OLD.req_qty_venue
    OR NEW.leg_role      <> OLD.leg_role THEN
        RAISE EXCEPTION 'order %: kolom identitas immutable', OLD.order_id USING ERRCODE = 'AR005';
    END IF;

    r_old := trd.fn_order_status_rank(OLD.status);
    r_new := trd.fn_order_status_rank(NEW.status);

    IF r_old = 9 THEN
        IF NEW.status <> OLD.status THEN
            IF r_new = 9 THEN                     -- dua laporan TERMINAL berbeda = anomali nyata
                NEW.status_conflicts := OLD.status_conflicts + 1;
            END IF;                               -- non-terminal terlambat = normal, bukan konflik
            NEW.status := OLD.status;             -- terminal pertama yang menang
        END IF;
    ELSIF r_new < r_old THEN                      -- laporan terlambat (mis. ACK REST setelah FILLED WS)
        NEW.status := OLD.status;
    END IF;

    IF NEW.cum_filled_qty_venue < OLD.cum_filled_qty_venue THEN
        NEW.cum_filled_qty_venue := OLD.cum_filled_qty_venue;
        NEW.avg_fill_px          := OLD.avg_fill_px;
    END IF;

    -- first-write-wins untuk identitas & timestamp milestone
    NEW.exchange_order_id := COALESCE(OLD.exchange_order_id, NEW.exchange_order_id);
    NEW.sent_at           := COALESCE(OLD.sent_at, NEW.sent_at);
    NEW.acked_at          := COALESCE(OLD.acked_at, NEW.acked_at);
    NEW.venue_accept_at   := COALESCE(OLD.venue_accept_at, NEW.venue_accept_at);
    NEW.reject_code       := COALESCE(NEW.reject_code, OLD.reject_code);
    NEW.reject_reason     := COALESCE(NEW.reject_reason, OLD.reject_reason);

    IF trd.fn_order_status_rank(NEW.status) = 9 THEN
        NEW.terminal_at := COALESCE(OLD.terminal_at, NEW.terminal_at, now());
    END IF;
    NEW.updated_at := now();
    RETURN NEW;
END
$$;

CREATE TRIGGER order_leg_merge
BEFORE INSERT OR UPDATE ON trd.order_leg
FOR EACH ROW EXECUTE FUNCTION trd.trg_order_leg_merge();

-- ---------------------------------------------------------------------
-- Fill: validasi terhadap order + konversi canonical
-- ---------------------------------------------------------------------
CREATE FUNCTION trd.trg_fill_enrich() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_instrument integer;
    v_side       text;
    v_mult       numeric;
BEGIN
    SELECT ol.instrument_id, ol.side INTO v_instrument, v_side
    FROM trd.order_leg ol WHERE ol.order_id = NEW.order_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'fill %: order % tidak ada', NEW.venue_trade_id, NEW.order_id USING ERRCODE = 'AR006';
    END IF;

    NEW.instrument_id := COALESCE(NEW.instrument_id, v_instrument);
    NEW.side          := COALESCE(NEW.side, v_side);

    IF NEW.instrument_id <> v_instrument OR NEW.side <> v_side THEN
        RAISE EXCEPTION 'fill %: instrumen/side (%/%) beda dengan order (%/%)',
            NEW.venue_trade_id, NEW.instrument_id, NEW.side, v_instrument, v_side
            USING ERRCODE = 'AR006';
    END IF;

    SELECT base_multiplier INTO v_mult FROM ref.instrument WHERE instrument_id = NEW.instrument_id;
    NEW.qty_base  := NEW.qty_venue * v_mult;
    NEW.quote_qty := NEW.px * NEW.qty_venue;
    RETURN NEW;
END
$$;

CREATE TRIGGER fill_enrich
BEFORE INSERT ON trd.fill
FOR EACH ROW EXECUTE FUNCTION trd.trg_fill_enrich();

-- Fill bersifat append-only.
CREATE FUNCTION trd.trg_fill_immutable() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'trd.fill append-only (fill_id %)', OLD.fill_id USING ERRCODE = 'AR006';
END
$$;

CREATE TRIGGER fill_immutable
BEFORE UPDATE OR DELETE ON trd.fill
FOR EACH ROW EXECUTE FUNCTION trd.trg_fill_immutable();

-- ---------------------------------------------------------------------
-- Partisi harian trd.opportunity (UTC). Panggil saat startup + harian.
-- Buat partisi SEBELUM harinya tiba: jika baris sudah masuk ke partisi
-- default untuk rentang yang sama, CREATE PARTITION akan gagal.
-- ---------------------------------------------------------------------
CREATE FUNCTION trd.fn_ensure_opportunity_partitions(
    p_start date    DEFAULT (now() AT TIME ZONE 'UTC')::date,
    p_days  integer DEFAULT 7)
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    d       date;
    v_name  text;
    v_count integer := 0;
BEGIN
    FOR i IN 0 .. p_days - 1 LOOP
        d := p_start + i;
        v_name := 'opportunity_' || to_char(d, 'YYYYMMDD');
        IF to_regclass('trd.' || v_name) IS NULL THEN
            EXECUTE format(
                'CREATE TABLE trd.%I PARTITION OF trd.opportunity FOR VALUES FROM (%L) TO (%L)',
                v_name, d::text || ' 00:00:00+00', (d + 1)::text || ' 00:00:00+00');
            v_count := v_count + 1;
        END IF;
    END LOOP;
    RETURN v_count;
END
$$;

CREATE FUNCTION trd.fn_drop_opportunity_partitions(
    p_keep_days integer,
    p_ref_date  date DEFAULT (now() AT TIME ZONE 'UTC')::date)
RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    r       record;
    v_count integer := 0;
BEGIN
    FOR r IN
        SELECT c.relname
        FROM pg_inherits i
        JOIN pg_class c     ON c.oid = i.inhrelid
        JOIN pg_class p     ON p.oid = i.inhparent
        JOIN pg_namespace n ON n.oid = p.relnamespace
        WHERE n.nspname = 'trd' AND p.relname = 'opportunity'
          AND c.relname ~ '^opportunity_[0-9]{8}$'
    LOOP
        IF to_date(substr(r.relname, 13), 'YYYYMMDD') < p_ref_date - p_keep_days THEN
            EXECUTE format('DROP TABLE trd.%I', r.relname);
            v_count := v_count + 1;
        END IF;
    END LOOP;
    RETURN v_count;
END
$$;

COMMIT;
