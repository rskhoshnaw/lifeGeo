-- NOT EXECUTED
-- Abort on unexpected existing state. Application Script for Customer API Slice Deployment

\set ON_ERROR_STOP on

-- Safeguard: Hard Precondition Checks
DO $$
DECLARE
    db_name text;
    pg_avail boolean;
    dev_tenants int;
    dev_admin_roles int;
BEGIN
    -- 1. Database Check
    SELECT current_database() INTO db_name;
    IF db_name != 'lifedrop_dev' THEN
        RAISE EXCEPTION 'Target database is %, expected lifedrop_dev. Aborting.', db_name;
    END IF;

    -- 2. PostGIS Check
    SELECT EXISTS(SELECT 1 FROM pg_available_extensions WHERE name = 'postgis') INTO pg_avail;
    IF NOT pg_avail THEN
        RAISE EXCEPTION 'PostGIS extension is not available. Aborting.';
    END IF;

    -- 3. Tenant Check
    SELECT COUNT(*) INTO dev_tenants FROM tenants WHERE code = 'dev';
    IF dev_tenants != 1 THEN
        RAISE EXCEPTION 'Expected exactly 1 dev tenant, found %. Aborting.', dev_tenants;
    END IF;

        -- 3.5 DevAdmin Role Check
    SELECT COUNT(*) INTO dev_admin_roles FROM roles r JOIN tenants t ON r.tenant_id = t.id WHERE r.name = 'DevAdmin' AND t.code = 'dev';
    IF dev_admin_roles != 1 THEN
        RAISE EXCEPTION 'Expected exactly 1 DevAdmin role for dev tenant, found %. Aborting.', dev_admin_roles;
    END IF;

    -- 4. Customer Table Check
    IF EXISTS (SELECT FROM pg_tables WHERE tablename = 'customers') THEN
        RAISE EXCEPTION 'Table "customers" already exists. Aborting safely to prevent conflicts.';
    END IF;

    -- 5. Role Check
    IF EXISTS (SELECT FROM pg_roles WHERE rolname = 'lifedrop_app') THEN
        RAISE EXCEPTION 'Role "lifedrop_app" already exists. Aborting safely.';
    END IF;
END $$;

BEGIN;

-- Apply Migration 0004 (customers)
CREATE EXTENSION IF NOT EXISTS postgis;

CREATE TABLE customers (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    type text NOT NULL,
    code text NOT NULL,
    name text NOT NULL,
    phone text,
    address text,
    city text,
    region text,
    geom geography(Point, 4326),
    location_source text,
    location_updated_at timestamptz,
    location_updated_by uuid,
    status text NOT NULL DEFAULT 'no_location',
    owner_user_id uuid,
    created_at timestamptz NOT NULL DEFAULT now(),

    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, code),
    FOREIGN KEY (tenant_id, owner_user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, location_updated_by) REFERENCES users(tenant_id, id),
    
    CONSTRAINT chk_customer_location_status CHECK (
        (geom IS NULL AND status = 'no_location') OR 
        (geom IS NOT NULL AND status != 'no_location')
    )
);

CREATE INDEX customers_geom_idx ON customers USING GIST (geom);
CREATE INDEX customers_tenant_idx ON customers (tenant_id);

ALTER TABLE customers ENABLE ROW LEVEL SECURITY;
ALTER TABLE customers FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON customers
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);


-- Apply Migration 0005 (dev admin permissions)
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'customers.read'
FROM roles r
JOIN tenants t ON r.tenant_id = t.id
WHERE r.name = 'DevAdmin' AND t.code = 'dev'
ON CONFLICT DO NOTHING;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'customers.write'
FROM roles r
JOIN tenants t ON r.tenant_id = t.id
WHERE r.name = 'DevAdmin' AND t.code = 'dev'
ON CONFLICT DO NOTHING;

-- Verification of Migration 0005 (Postcondition)
DO $$
DECLARE
    read_perm int;
    write_perm int;
BEGIN
    SELECT COUNT(*) INTO read_perm
    FROM role_permissions rp
    JOIN roles r ON rp.role_id = r.id
    JOIN tenants t ON rp.tenant_id = t.id
    WHERE r.name = 'DevAdmin' AND t.code = 'dev' AND rp.permission_key = 'customers.read';

    SELECT COUNT(*) INTO write_perm
    FROM role_permissions rp
    JOIN roles r ON rp.role_id = r.id
    JOIN tenants t ON rp.tenant_id = t.id
    WHERE r.name = 'DevAdmin' AND t.code = 'dev' AND rp.permission_key = 'customers.write';

    IF read_perm != 1 OR write_perm != 1 THEN
        RAISE EXCEPTION 'Verification failed: DevAdmin is missing customers.read or customers.write permissions. Aborting.';
    END IF;
END $$;

-- Create non-superuser runtime role securely (NOLOGIN initially to prevent unsecured access)
CREATE ROLE lifedrop_app WITH NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;


-- Apply strictly proven rehearsal table privileges
GRANT USAGE ON SCHEMA public TO lifedrop_app;

-- Select grants
GRANT SELECT ON 
    tenants, 
    users, 
    roles, 
    role_permissions, 
    user_roles, 
    devices, 
    refresh_tokens, 
    customers, 
    audit_logs 
TO lifedrop_app;

-- Insert grants
GRANT INSERT ON 
    user_roles, 
    audit_logs, 
    auth_login_audit, 
    devices, 
    refresh_tokens, 
    customers 
TO lifedrop_app;

-- Update grants
GRANT UPDATE ON 
    devices, 
    refresh_tokens 
TO lifedrop_app;

COMMIT;

SELECT 'Migration completed successfully. Role created but NOT LOGGED IN. Proceed to manual password setup.' AS status;


