-- Places hierarchy and the customer profile (plan step 1.23-b, owner decisions 2026-10-05/06).
-- One table, one tree per company: city -> neighbourhood -> street -> alley. A neighbourhood belongs to a city, a street
-- to a neighbourhood, an alley to a street (or straight to a neighbourhood).
-- Any employee (customers.write) may add a neighbourhood, street or alley: it is part of the reference at once, there is
-- no approval step. Managers (places.manage: SalesManager, Supervisor) fix spelling, move, deactivate, merge and add
-- cities. The tree can be exported for other projects.
--   places.kind   : city | neighbourhood | street | alley
--   places.status : active | inactive     (merged_into is set when a duplicate was merged into another)
-- A customer points at the most precise place it knows (customers.place_id); the chain above it is read from the tree.
CREATE TABLE places (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    parent_id uuid,
    kind text NOT NULL CHECK (kind IN ('city', 'neighbourhood', 'street', 'alley')),
    name text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 160),
    name_ckb text CHECK (char_length(name_ckb) <= 160),
    name_ar text CHECK (char_length(name_ar) <= 160),
    name_en text CHECK (char_length(name_en) <= 160),
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    created_by uuid,
    merged_into uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    -- Columns of the uniqueness rule: one live name per kind under one parent (cities share the "no parent" key).
    parent_key uuid GENERATED ALWAYS AS (coalesce(parent_id, '00000000-0000-0000-0000-000000000000'::uuid)) STORED,
    name_key text GENERATED ALWAYS AS (lower(btrim(name))) STORED,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, parent_id) REFERENCES places(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, merged_into) REFERENCES places(tenant_id, id),
    CONSTRAINT places_not_own_parent CHECK (parent_id IS NULL OR parent_id <> id),
    CONSTRAINT places_not_merged_into_itself CHECK (merged_into IS NULL OR merged_into <> id)
);
-- A merged row keeps its name without blocking a new live one.
CREATE UNIQUE INDEX places_name_unique ON places (tenant_id, parent_key, kind, name_key) WHERE merged_into IS NULL;
CREATE INDEX places_parent_idx ON places (tenant_id, parent_id, kind);

-- The tree rules: a city has no parent; the parent of a neighbourhood is a city, of a street a neighbourhood, of an
-- alley a street or a neighbourhood. The kind of a row never changes.
CREATE FUNCTION places_check_hierarchy() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    parent_kind text;
BEGIN
    IF TG_OP = 'UPDATE' AND NEW.kind <> OLD.kind THEN
        RAISE EXCEPTION 'the kind of a place cannot change' USING ERRCODE = '23514';
    END IF;
    IF NEW.parent_id IS NULL THEN
        IF NEW.kind <> 'city' THEN RAISE EXCEPTION 'a % needs a parent', NEW.kind USING ERRCODE = '23514'; END IF;
        RETURN NEW;
    END IF;
    SELECT kind INTO parent_kind FROM places WHERE tenant_id = NEW.tenant_id AND id = NEW.parent_id;
    IF NOT ((NEW.kind = 'neighbourhood' AND parent_kind = 'city')
         OR (NEW.kind = 'street' AND parent_kind = 'neighbourhood')
         OR (NEW.kind = 'alley' AND parent_kind IN ('street', 'neighbourhood'))) THEN
        RAISE EXCEPTION 'a % cannot be under a %', NEW.kind, parent_kind USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER places_hierarchy BEFORE INSERT OR UPDATE OF parent_id, kind ON places
FOR EACH ROW EXECUTE FUNCTION places_check_hierarchy();

ALTER TABLE customers
    ADD COLUMN place_id uuid,
    ADD CONSTRAINT customers_place_fk FOREIGN KEY (tenant_id, place_id) REFERENCES places(tenant_id, id),
    ADD CONSTRAINT customers_phone_length CHECK (char_length(phone) <= 40),
    ADD CONSTRAINT customers_address_length CHECK (char_length(address) <= 300);
CREATE INDEX customers_place_idx ON customers (tenant_id, place_id) WHERE place_id IS NOT NULL;

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
