-- Merge user accounts: moves everything owned by :from_email to :to_email, then deactivates :from_email.
-- Run on the database (psql). Dry run by default (rolls back); pass -v commit=1 to keep the changes.
--   psql "$DATABASE_URL" -v from_email=old@example.com -v to_email=keep@example.com -v commit=0 -f scripts/db/merge-user.sql
-- Rules:
--  * Customers, visits, shifts, tracking, orders, payments, samples, plans, follow-ups ... (every column that points to users) move to the kept user.
--  * The kept user's own roles, team, saved reports names, channels stay; duplicates of the old user are dropped (reported as NOTICE).
--  * Devices of the old user move to the kept user (a device registered for both is merged into the kept user's row).
--  * Old user's login sessions (refresh tokens) are deleted, so it has to sign in again as the kept user.
--  * Append-only history (audit_logs, auth_login_audit, visit_status_history, platform_audit_logs) cannot be edited by design, so the old
--    user row cannot be deleted; it is deactivated and its e-mail is renamed (frees the address). History keeps showing the old account.
\set ON_ERROR_STOP on
BEGIN;
SELECT set_config('merge.from', lower(:'from_email'), true), set_config('merge.to', lower(:'to_email'), true);
DO $merge$
DECLARE
    t uuid; f uuid; fe text := current_setting('merge.from'); te text := current_setting('merge.to');
    r record; n bigint; d record;
BEGIN
    SELECT tenant_id, id INTO t, f FROM users WHERE lower(email) = fe;
    IF f IS NULL THEN RAISE EXCEPTION 'from user % not found', fe; END IF;
    SELECT id INTO d FROM users WHERE lower(email) = te AND tenant_id = t;
    IF NOT FOUND THEN RAISE EXCEPTION 'to user % not found in the same company', te; END IF;
    DECLARE k uuid := d.id; BEGIN
    IF k = f THEN RAISE EXCEPTION 'same user'; END IF;
    IF EXISTS (SELECT 1 FROM shifts WHERE tenant_id = t AND user_id = f AND ended_at IS NULL)
       AND EXISTS (SELECT 1 FROM shifts WHERE tenant_id = t AND user_id = k AND ended_at IS NULL)
       THEN RAISE EXCEPTION 'both users have an open shift: close one first'; END IF;

    -- sessions of the old user
    DELETE FROM refresh_tokens WHERE tenant_id = t AND user_id = f;

    -- rows that would collide with the kept user's rows
    DELETE FROM user_roles x WHERE x.tenant_id = t AND x.user_id = f;
    GET DIAGNOSTICS n = ROW_COUNT; RAISE NOTICE 'user_roles of old user dropped (kept user keeps own roles): %', n;
    DELETE FROM team_members x WHERE x.tenant_id = t AND x.user_id = f AND EXISTS (SELECT 1 FROM team_members y WHERE y.tenant_id = t AND y.user_id = k);
    DELETE FROM warehouse_staff x WHERE x.tenant_id = t AND x.user_id = f AND EXISTS (SELECT 1 FROM warehouse_staff y WHERE y.tenant_id = t AND y.user_id = k AND y.warehouse_id = x.warehouse_id);
    DELETE FROM user_channels x WHERE x.tenant_id = t AND x.user_id = f AND EXISTS (SELECT 1 FROM user_channels y WHERE y.tenant_id = t AND y.user_id = k AND y.channel = x.channel);
    DELETE FROM channel_link_codes x WHERE x.tenant_id = t AND x.user_id = f AND EXISTS (SELECT 1 FROM channel_link_codes y WHERE y.tenant_id = t AND y.user_id = k AND y.channel = x.channel);
    DELETE FROM sales_targets x WHERE x.tenant_id = t AND x.user_id = f AND EXISTS (SELECT 1 FROM sales_targets y WHERE y.tenant_id = t AND y.user_id = k AND y.product_id IS NOT DISTINCT FROM x.product_id AND y.month = x.month);
    UPDATE saved_reports x SET name = x.name || ' (' || left(f::text, 4) || ')' WHERE x.tenant_id = t AND x.owner_user_id = f AND EXISTS (SELECT 1 FROM saved_reports y WHERE y.tenant_id = t AND y.owner_user_id = k AND y.name = x.name);
    -- the same phone registered on both accounts: point everything at the kept user's device row, drop the duplicate
    FOR r IN SELECT o.id AS old_id, n2.id AS new_id FROM devices o JOIN devices n2 ON n2.tenant_id = o.tenant_id AND n2.user_id = k AND n2.install_id = o.install_id
             WHERE o.tenant_id = t AND o.user_id = f LOOP
        UPDATE tracking_points SET device_id = r.new_id WHERE tenant_id = t AND device_id = r.old_id;
        UPDATE visit_location_events SET device_id = r.new_id WHERE tenant_id = t AND device_id = r.old_id;
        UPDATE shifts SET device_id = r.new_id WHERE tenant_id = t AND device_id = r.old_id;
        DELETE FROM devices WHERE tenant_id = t AND id = r.old_id;
    END LOOP;

    -- every column that points to a user (except append-only history and sessions), by the foreign keys
    FOR r IN
        SELECT c.conrelid::regclass AS tbl, a.attname AS col
        FROM pg_constraint c JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey)
        WHERE c.contype = 'f' AND c.confrelid = 'users'::regclass AND a.attname NOT IN ('tenant_id', 'actor_tenant_id')
          AND c.conrelid::regclass::text NOT IN ('audit_logs', 'auth_login_audit', 'visit_status_history', 'platform_audit_logs', 'refresh_tokens')
        ORDER BY 1, 2
    LOOP
        EXECUTE format('UPDATE %s SET %I = $1 WHERE tenant_id = $2 AND %I = $3', r.tbl, r.col, r.col) USING k, t, f;
        GET DIAGNOSTICS n = ROW_COUNT;
        IF n > 0 THEN RAISE NOTICE 'moved % row(s) in %.%', n, r.tbl, r.col; END IF;
    END LOOP;
    -- a user column without a foreign key
    UPDATE shift_health SET user_id = k WHERE tenant_id = t AND user_id = f;

    -- fill empty fields of the kept user from the old one, then retire the old one
    UPDATE users u SET phone = COALESCE(u.phone, o.phone), city = COALESCE(u.city, o.city) FROM users o WHERE u.id = k AND o.id = f AND u.tenant_id = t AND o.tenant_id = t;
    UPDATE users SET is_active = false, email = 'merged-' || f::text || '@invalid.local' WHERE tenant_id = t AND id = f;
    RAISE NOTICE 'done: % -> %', fe, te;
    END;
END $merge$;
\if :commit
COMMIT;
\else
\echo DRY RUN: rolled back. Run again with -v commit=1 to keep the changes.
ROLLBACK;
\endif
