-- Data for the load test (plan step 5.70): one company "load" with N visitors, 3 managers, customers around Erbil, today's plan for every visitor, one warehouse and some products.
-- Ids are built from a kind and a number, so scenario.mjs knows them without asking the database: 00000000-0000-4000-<kind>-<n as 12 hex digits>.
--   psql -d lifedrop_load -v visitors=50 -f scripts/perf/seed-load.sql          (on a THROW-AWAY database made by deploy/setup-db.sh; never on real data)
\set ON_ERROR_STOP on
\if :{?visitors}
\else
\set visitors 50
\endif
CREATE FUNCTION pg_temp.lid(kind int, n int) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT ('00000000-0000-4000-' || lpad(kind::text, 4, '0') || '-' || lpad(to_hex(n), 12, '0'))::uuid $$;

INSERT INTO tenants (id, name, code) VALUES (pg_temp.lid(1, 1), 'Load', 'load');
SELECT seed_default_roles(pg_temp.lid(1, 1));
SELECT set_config('app.current_tenant', pg_temp.lid(1, 1)::text, false);

-- 3 managers (kind 2) and the visitors (kind 3)
INSERT INTO users (id, tenant_id) SELECT pg_temp.lid(2, n), pg_temp.lid(1, 1) FROM generate_series(1, 3) n;
INSERT INTO users (id, tenant_id) SELECT pg_temp.lid(3, n), pg_temp.lid(1, 1) FROM generate_series(1, :visitors) n;
INSERT INTO user_roles (tenant_id, user_id, role_id)
SELECT pg_temp.lid(1, 1), pg_temp.lid(2, n), r.id FROM generate_series(1, 3) n JOIN roles r ON r.tenant_id = pg_temp.lid(1, 1) AND r.name = 'SalesManager';
INSERT INTO user_roles (tenant_id, user_id, role_id)
SELECT pg_temp.lid(1, 1), pg_temp.lid(3, n), r.id FROM generate_series(1, :visitors) n JOIN roles r ON r.tenant_id = pg_temp.lid(1, 1) AND r.name = 'SalesRep';

-- a signed-in session with an approved phone for everybody (kind 4 devices, kind 5 session families)
INSERT INTO devices (id, tenant_id, user_id, device_label, platform, install_id, app_version, secret_hash, status, approved_at)
SELECT pg_temp.lid(4, n), pg_temp.lid(1, 1), pg_temp.lid(CASE WHEN n <= 3 THEN 2 ELSE 3 END, CASE WHEN n <= 3 THEN n ELSE n - 3 END), 'Load phone ' || n, 'android', 'install-' || n, '1.0.0',
       decode(md5('secret' || n), 'hex'), 'approved', now()
FROM generate_series(1, :visitors + 3) n;
INSERT INTO refresh_tokens (id, family_id, tenant_id, user_id, device_id, token_hash, expires_at)
SELECT pg_temp.lid(6, n), pg_temp.lid(5, n), pg_temp.lid(1, 1), pg_temp.lid(CASE WHEN n <= 3 THEN 2 ELSE 3 END, CASE WHEN n <= 3 THEN n ELSE n - 3 END), pg_temp.lid(4, n),
       decode(md5('token' || n), 'hex'), now() + interval '30 days'
FROM generate_series(1, :visitors + 3) n;

INSERT INTO regions (id, tenant_id, name) VALUES (pg_temp.lid(7, 1), pg_temp.lid(1, 1), 'Erbil');
INSERT INTO teams (id, tenant_id, region_id, name) VALUES (pg_temp.lid(8, 1), pg_temp.lid(1, 1), pg_temp.lid(7, 1), 'Team');
INSERT INTO team_members (tenant_id, user_id, team_id) SELECT pg_temp.lid(1, 1), pg_temp.lid(3, n), pg_temp.lid(8, 1) FROM generate_series(1, :visitors) n;
INSERT INTO places (id, tenant_id, kind, name) VALUES (pg_temp.lid(9, 1), pg_temp.lid(1, 1), 'city', 'Erbil');

-- 12 customers per visitor (kind 10; their places are a formula of the number, which scenario.mjs repeats), all approved with a location, and one planned visit each for today in Iraq (kind 11)
INSERT INTO customers (id, tenant_id, type, code, name, region_id, geom, location_source, location_updated_at, status, owner_user_id, approval_status, place_id)
SELECT pg_temp.lid(10, n), pg_temp.lid(1, 1), 'Pharmacy', 'C-' || n, 'Customer ' || n, pg_temp.lid(7, 1),
       ST_SetSRID(ST_MakePoint(44.0 + ((n * 7919) % 1000) / 10000.0, 36.15 + ((n * 104729) % 1000) / 10000.0), 4326)::geography, 'manual', now(), 'located', pg_temp.lid(3, ((n - 1) / 12) + 1), 'approved', pg_temp.lid(9, 1)
FROM generate_series(1, :visitors * 12) n;
INSERT INTO visits (id, tenant_id, user_id, customer_id, status, schedule_date, created_at)
SELECT pg_temp.lid(11, n), pg_temp.lid(1, 1), pg_temp.lid(3, ((n - 1) / 12) + 1), pg_temp.lid(10, n), 'Planned', (now() AT TIME ZONE 'Asia/Baghdad')::date, now()
FROM generate_series(1, :visitors * 12) n;

-- one company warehouse that serves the city, 40 products with plenty of stock
INSERT INTO warehouses (id, tenant_id, code, name, kind, city_id) VALUES (pg_temp.lid(12, 1), pg_temp.lid(1, 1), 'WL', 'Load warehouse', 'company', pg_temp.lid(9, 1));
INSERT INTO city_warehouses (tenant_id, city_id, warehouse_id) VALUES (pg_temp.lid(1, 1), pg_temp.lid(9, 1), pg_temp.lid(12, 1));
INSERT INTO products (id, tenant_id, code, name, unit, currency, base_price) SELECT pg_temp.lid(13, n), pg_temp.lid(1, 1), 'P-' || n, 'Product ' || n, 'box', 'IQD', 1000 + n * 250 FROM generate_series(1, 40) n;
INSERT INTO warehouse_stock (tenant_id, warehouse_id, product_id, quantity) SELECT pg_temp.lid(1, 1), pg_temp.lid(12, 1), pg_temp.lid(13, n), 1000000 FROM generate_series(1, 40) n;
