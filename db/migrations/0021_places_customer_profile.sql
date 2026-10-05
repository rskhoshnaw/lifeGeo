-- Reference list of cities, neighbourhoods and streets and the customer profile (plan step 1.23-b, owner request
-- 2026-10-05). The list is imported from a CSV (OpenStreetMap export), edited by managers and extended by field
-- users: a name a field user suggests is usable at once with status 'suggested' until a manager approves, renames,
-- merges or deactivates it. A customer points at its city, neighbourhood and street.
--   cities.status  : active | inactive
--   places.kind    : neighbourhood | street
--   places.status  : active | suggested | inactive   (merged_into is set when a duplicate was merged into another)
-- Permission places.manage: SalesManager and Supervisor. Reading uses customers.read, suggesting customers.write.
CREATE TABLE cities (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    name text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 120),
    name_ckb text CHECK (char_length(name_ckb) <= 120),
    name_ar text CHECK (char_length(name_ar) <= 120),
    name_en text CHECK (char_length(name_en) <= 120),
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id)
);
CREATE UNIQUE INDEX cities_name_unique ON cities (tenant_id, lower(btrim(name)));

CREATE TABLE places (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    city_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('neighbourhood', 'street')),
    name text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 160),
    name_ckb text CHECK (char_length(name_ckb) <= 160),
    name_ar text CHECK (char_length(name_ar) <= 160),
    name_en text CHECK (char_length(name_en) <= 160),
    osm_ref text CHECK (char_length(osm_ref) <= 40),
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'suggested', 'inactive')),
    suggested_by uuid,
    merged_into uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, city_id) REFERENCES cities(tenant_id, id),
    FOREIGN KEY (tenant_id, suggested_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, merged_into) REFERENCES places(tenant_id, id),
    CONSTRAINT places_not_merged_into_itself CHECK (merged_into IS NULL OR merged_into <> id)
);
-- One live row per name in a city and kind; a merged row keeps its name without blocking a new one.
CREATE UNIQUE INDEX places_name_unique ON places (tenant_id, city_id, kind, lower(btrim(name))) WHERE merged_into IS NULL;
CREATE INDEX places_city_idx ON places (tenant_id, city_id, kind);

ALTER TABLE customers
    ADD COLUMN city_id uuid,
    ADD COLUMN neighbourhood_id uuid,
    ADD COLUMN street_id uuid,
    ADD CONSTRAINT customers_city_fk FOREIGN KEY (tenant_id, city_id) REFERENCES cities(tenant_id, id),
    ADD CONSTRAINT customers_neighbourhood_fk FOREIGN KEY (tenant_id, neighbourhood_id) REFERENCES places(tenant_id, id),
    ADD CONSTRAINT customers_street_fk FOREIGN KEY (tenant_id, street_id) REFERENCES places(tenant_id, id),
    ADD CONSTRAINT customers_phone_length CHECK (char_length(phone) <= 40),
    ADD CONSTRAINT customers_address_length CHECK (char_length(address) <= 300);
CREATE INDEX customers_street_idx ON customers (tenant_id, street_id) WHERE street_id IS NOT NULL;
CREATE INDEX customers_neighbourhood_idx ON customers (tenant_id, neighbourhood_id) WHERE neighbourhood_id IS NOT NULL;

ALTER TABLE cities ENABLE ROW LEVEL SECURITY;
ALTER TABLE cities FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON cities
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE places ENABLE ROW LEVEL SECURITY;
ALTER TABLE places FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON places
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

CREATE OR REPLACE FUNCTION seed_default_roles(p_tenant uuid) RETURNS void LANGUAGE sql AS $$
    WITH defaults(name, data_scope, requires_device_binding) AS (
        VALUES ('CompanyManager', 'tenant', false),
               ('SalesManager', 'tenant', false),
               ('Supervisor', 'region', false),
               ('FinanceManager', 'tenant', false),
               ('SalesRep', 'own', true),
               ('ScientificRep', 'own', true),
               ('WarehouseManager', 'own', true),
               ('Driver', 'own', true)
    )
    INSERT INTO roles (id, tenant_id, name, data_scope, requires_device_binding)
    SELECT gen_random_uuid(), p_tenant, d.name, d.data_scope, d.requires_device_binding FROM defaults d
    ON CONFLICT (tenant_id, name) DO NOTHING;

    WITH grants(role_name, permission_key) AS (
        VALUES ('CompanyManager', 'customers.read'), ('CompanyManager', 'audit.read'), ('CompanyManager', 'regions.read'), ('CompanyManager', 'teams.read'),
               ('SalesManager', 'customers.read'), ('SalesManager', 'customers.write'), ('SalesManager', 'customers.location.write'), ('SalesManager', 'customers.geofence.write'), ('SalesManager', 'visits.write'), ('SalesManager', 'visits.cancel'), ('SalesManager', 'customers.approve'),
               ('SalesManager', 'regions.read'), ('SalesManager', 'regions.manage'),
               ('SalesManager', 'teams.read'), ('SalesManager', 'teams.manage'),
               ('SalesManager', 'tracking.read'), ('SalesManager', 'places.manage'),
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'customers.location.write'), ('Supervisor', 'customers.geofence.write'), ('Supervisor', 'visits.write'), ('Supervisor', 'visits.cancel'), ('Supervisor', 'customers.approve'),
               ('Supervisor', 'regions.read'), ('Supervisor', 'teams.read'), ('Supervisor', 'tracking.read'), ('Supervisor', 'places.manage'),
               ('FinanceManager', 'customers.read'), ('FinanceManager', 'audit.read'), ('FinanceManager', 'tracking.summary'),
               ('SalesRep', 'customers.read'), ('SalesRep', 'customers.write'), ('SalesRep', 'customers.location.write'), ('SalesRep', 'visits.fail'), ('SalesRep', 'visits.create_unplanned'), ('SalesRep', 'visits.plan'),
               ('ScientificRep', 'customers.read'), ('ScientificRep', 'visits.fail'), ('ScientificRep', 'visits.create_unplanned'), ('ScientificRep', 'visits.plan')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'places.manage' FROM roles r WHERE r.name IN ('SalesManager', 'Supervisor')
ON CONFLICT DO NOTHING;
