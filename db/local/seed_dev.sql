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
    ('00000000-0000-0000-0000-000000000105', '00000000-0000-0000-0000-00000000d001', 'rep2@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g='),
    ('00000000-0000-0000-0000-000000000106', '00000000-0000-0000-0000-00000000d001', 'warehouse@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g='),
    ('00000000-0000-0000-0000-000000000107', '00000000-0000-0000-0000-00000000d001', 'driver@dev.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g=');

INSERT INTO user_roles (tenant_id, user_id, role_id)
SELECT '00000000-0000-0000-0000-00000000d001', u.id, r.id
FROM (VALUES ('00000000-0000-0000-0000-000000000101'::uuid, 'SalesManager'), ('00000000-0000-0000-0000-000000000102'::uuid, 'Supervisor'), ('00000000-0000-0000-0000-000000000103'::uuid, 'FinanceManager'),
             ('00000000-0000-0000-0000-000000000104'::uuid, 'SalesRep'), ('00000000-0000-0000-0000-000000000105'::uuid, 'SalesRep'),
             ('00000000-0000-0000-0000-000000000106'::uuid, 'WarehouseManager'), ('00000000-0000-0000-0000-000000000107'::uuid, 'Driver')) AS u(id, role_name)
JOIN roles r ON r.tenant_id = '00000000-0000-0000-0000-00000000d001' AND r.name = u.role_name;

-- Super Admin of the platform company (step 3.1; company code: platform, same password).
INSERT INTO users (id, tenant_id, email, password_hash) VALUES
    ('00000000-0000-0000-0000-0000000001f1', '00000000-0000-0000-0000-00000000f001', 'root@platform.local', 'pbkdf2-sha256$210000$bGlmZWRyb3AtbG9jYWwtMQ==$7FTGKnaSVxsMFfZkOXOofr05WMJvL1v+4uNKqG1ih8g=');
INSERT INTO user_roles (tenant_id, user_id, role_id)
SELECT '00000000-0000-0000-0000-00000000f001', '00000000-0000-0000-0000-0000000001f1', id FROM roles WHERE tenant_id = '00000000-0000-0000-0000-00000000f001' AND name = 'SuperAdmin';

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

-- Sample places tree (plan step 1.23-b): the cities and a few Erbil neighbourhoods so the pickers are not empty. The same
-- names are in docs/places-starter.csv, which a manager imports in the panel's Places tab (Import CSV).
INSERT INTO places (id, tenant_id, parent_id, kind, name, name_ckb, name_ar, name_en) VALUES
    ('00000000-0000-0000-0000-0000000005c1', '00000000-0000-0000-0000-00000000d001', NULL, 'city', 'Erbil', 'هەولێر', 'أربيل', 'Erbil'),
    ('00000000-0000-0000-0000-0000000005c2', '00000000-0000-0000-0000-00000000d001', NULL, 'city', 'Sulaymaniyah', 'سلێمانی', 'السليمانية', 'Sulaymaniyah');
INSERT INTO places (tenant_id, parent_id, kind, name, name_ckb, name_ar, name_en) VALUES
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-0000000005c1', 'neighbourhood', 'Ankawa', 'عەنکاوە', 'عنكاوا', 'Ankawa'),
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-0000000005c1', 'neighbourhood', 'Bakhtiari', 'بەختیاری', 'بختياري', 'Bakhtiari');

-- Sample catalogue and warehouse (plan steps 2.1, 2.2) so an order can be tried at once: two products in IQD, a city price for
-- Erbil, one warehouse. Sample customers have no place, so they pay the general price until a place is chosen for them.
INSERT INTO products (id, tenant_id, code, name, name_ckb, name_ar, name_en, unit, currency, base_price) VALUES
    ('00000000-0000-0000-0000-0000000006a1', '00000000-0000-0000-0000-00000000d001', 'PAR-500', 'Paracetamol 500 mg', 'پاراسیتامۆل ٥٠٠', 'باراسيتامول 500', 'Paracetamol 500 mg', 'box', 'IQD', 1500),
    ('00000000-0000-0000-0000-0000000006a2', '00000000-0000-0000-0000-00000000d001', 'AMX-250', 'Amoxicillin 250 mg', 'ئامۆکسیسیلین ٢٥٠', 'أموكسيسيلين 250', 'Amoxicillin 250 mg', 'box', 'IQD', 4000);
INSERT INTO city_prices (tenant_id, product_id, city_id, price, valid_from) VALUES
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-0000000006a1', '00000000-0000-0000-0000-0000000005c1', 1800, '2026-01-01');
INSERT INTO warehouses (id, tenant_id, code, name) VALUES
    ('00000000-0000-0000-0000-0000000006b1', '00000000-0000-0000-0000-00000000d001', 'W1', 'Erbil warehouse');
INSERT INTO warehouse_staff (tenant_id, warehouse_id, user_id) VALUES
    ('00000000-0000-0000-0000-00000000d001', '00000000-0000-0000-0000-0000000006b1', '00000000-0000-0000-0000-000000000106');
