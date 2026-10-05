-- Local development data (docs/LOCAL-SETUP.md). Never apply to a real database.
-- Company code: dev. Every user's password: LifeDrop-Local-1
DO $$ BEGIN
    IF current_database() <> 'lifedrop_dev' THEN
        RAISE EXCEPTION 'Refusing to seed a database other than lifedrop_dev';
    END IF;
END $$;

INSERT INTO tenants (id, name, code) VALUES ('00000000-0000-0000-0000-00000000d001', 'LifeDrop Local', 'dev');
SELECT seed_default_roles('00000000-0000-0000-0000-00000000d001');

INSERT INTO users (id, tenant_id, email, password_hash) VALUES
    ('00000000-0000-0000-0000-000000000101', '00000000-0000-0000-0000-00000000d001', 'manager@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g='),
    ('00000000-0000-0000-0000-000000000102', '00000000-0000-0000-0000-00000000d001', 'supervisor@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g='),
    ('00000000-0000-0000-0000-000000000103', '00000000-0000-0000-0000-00000000d001', 'finance@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g='),
    ('00000000-0000-0000-0000-000000000104', '00000000-0000-0000-0000-00000000d001', 'rep1@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g='),
    ('00000000-0000-0000-0000-000000000105', '00000000-0000-0000-0000-00000000d001', 'rep2@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g=');

INSERT INTO user_roles (tenant_id, user_id, role_id)
SELECT '00000000-0000-0000-0000-00000000d001', u.id, r.id
FROM (VALUES ('00000000-0000-0000-0000-000000000101'::uuid, 'SalesManager'), ('00000000-0000-0000-0000-000000000102'::uuid, 'Supervisor'), ('00000000-0000-0000-0000-000000000103'::uuid, 'FinanceManager'),
             ('00000000-0000-0000-0000-000000000104'::uuid, 'SalesRep'), ('00000000-0000-0000-0000-000000000105'::uuid, 'SalesRep')) AS u(id, role_name)
JOIN roles r ON r.tenant_id = '00000000-0000-0000-0000-00000000d001' AND r.name = u.role_name;

-- Tables below use row-level security by tenant.
SELECT set_config('app.current_tenant', '00000000-0000-0000-0000-00000000d001', false);

INSERT INTO regions (id, tenant_id, name) VALUES
    ('00000000-0000-0000-0000-0000000002e1', '00000000-0000-0000-0000-00000000d001', 'Erbil'),
    ('00000000-0000-0000-0000-0000000002e2', '00000000-0000-0000-0000-00000000d001', 'Sulaymaniyah');
INSERT INTO teams (id, tenant_id, region_id, name) VALUES
    ('00000000-0000-0000-0000-0000000003a1', '00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-0000000002e1', 'Erbil team'),
    ('00000000-0000-0000-0000-0000000003a2', '00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-0000000002e2', 'Sulaymaniyah team');
INSERT INTO team_members (tenant_id, user_id, team_id) VALUES
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-000000000102', '00000000-0000-0000-0000-0000000003a1'),
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-000000000104', '00000000-0000-0000-0000-0000000003a1'),
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-000000000105', '00000000-0000-0000-0000-0000000003a2');

-- Approved customers with a location (default 20 m check-in radius). Move one to where you test with the phone:
-- in the panel, or UPDATE customers SET geom = ST_GeogFromText('SRID=4326;POINT(<lon> <lat>)') WHERE code = 'P-001';
INSERT INTO customers (id, tenant_id, type, code, name, city, region_id, geom, location_source, location_updated_at, status, owner_user_id) VALUES
    ('00000000-0000-0000-0000-0000000004c1', '00000000-0000-0000-0000-00000000d001', 'Pharmacy', 'P-001', 'Shifa Pharmacy', 'Erbil', '00000000-0000-0000-0000-0000000002e1',
        ST_GeogFromText('SRID=4326;POINT(44.0092 36.1911)'), 'manual', now(), 'located', '00000000-0000-0000-0000-000000000104'),
    ('00000000-0000-0000-0000-0000000004c2', '00000000-0000-0000-0000-00000000d001', 'Doctor', 'D-001', 'Dr. Karwan Clinic', 'Erbil', '00000000-0000-0000-0000-0000000002e1',
        ST_GeogFromText('SRID=4326;POINT(44.0210 36.1990)'), 'manual', now(), 'located', '00000000-0000-0000-0000-000000000104'),
    ('00000000-0000-0000-0000-0000000004c3', '00000000-0000-0000-0000-00000000d001', 'Wholesaler', 'W-001', 'Slemani Medical Store', 'Sulaymaniyah', '00000000-0000-0000-0000-0000000002e2',
        ST_GeogFromText('SRID=4326;POINT(45.4329 35.5613)'), 'manual', now(), 'located', '00000000-0000-0000-0000-000000000105');
