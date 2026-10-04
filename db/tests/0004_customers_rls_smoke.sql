-- Run only on a disposable PostGIS database after migrations 0001-0004.
-- All probe data and the temporary owner role are rolled back.
\set ON_ERROR_STOP on
BEGIN;

INSERT INTO tenants(id, name, code) VALUES
  ('11111111-1111-4111-8111-111111111111', 'RLS Probe A', 'rls-probe-a'),
  ('22222222-2222-4222-8222-222222222222', 'RLS Probe B', 'rls-probe-b');

CREATE ROLE customers_rls_probe NOLOGIN;
ALTER TABLE customers OWNER TO customers_rls_probe;
SET ROLE customers_rls_probe;

DO $$
BEGIN
  IF (SELECT count(*) FROM customers) <> 0 THEN
    RAISE EXCEPTION 'Table owner bypassed RLS without tenant context';
  END IF;
END $$;

SELECT set_config('app.current_tenant', '11111111-1111-4111-8111-111111111111', true);
INSERT INTO customers(id, tenant_id, type, code, name) VALUES
  ('aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', '11111111-1111-4111-8111-111111111111', 'probe', 'A-1', 'Probe A');

SELECT set_config('app.current_tenant', '22222222-2222-4222-8222-222222222222', true);
INSERT INTO customers(id, tenant_id, type, code, name) VALUES
  ('bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', '22222222-2222-4222-8222-222222222222', 'probe', 'B-1', 'Probe B');

DO $$
BEGIN
  IF (SELECT count(*) FROM customers) <> 1 OR
     (SELECT code FROM customers LIMIT 1) <> 'B-1' THEN
    RAISE EXCEPTION 'Tenant B read isolation failed';
  END IF;
END $$;

SELECT set_config('app.current_tenant', '11111111-1111-4111-8111-111111111111', true);
DO $$
DECLARE
  affected integer;
  insert_blocked boolean := false;
BEGIN
  IF (SELECT count(*) FROM customers) <> 1 OR
     (SELECT code FROM customers LIMIT 1) <> 'A-1' THEN
    RAISE EXCEPTION 'Tenant A read isolation failed';
  END IF;

  UPDATE customers SET name = 'Cross-tenant update' WHERE code = 'B-1';
  GET DIAGNOSTICS affected = ROW_COUNT;
  IF affected <> 0 THEN
    RAISE EXCEPTION 'Cross-tenant update succeeded';
  END IF;

  BEGIN
    INSERT INTO customers(id, tenant_id, type, code, name) VALUES
      ('cccccccc-cccc-4ccc-8ccc-cccccccccccc',
       '22222222-2222-4222-8222-222222222222', 'probe', 'B-2', 'Cross-tenant insert');
  EXCEPTION WHEN insufficient_privilege THEN
    insert_blocked := true;
  END;
  IF NOT insert_blocked THEN
    RAISE EXCEPTION 'Cross-tenant insert succeeded';
  END IF;
END $$;

SELECT set_config('app.current_tenant', '', true);
DO $$
BEGIN
  IF (SELECT count(*) FROM customers) <> 0 THEN
    RAISE EXCEPTION 'Missing tenant context exposed rows';
  END IF;
END $$;

RESET ROLE;
ROLLBACK;
