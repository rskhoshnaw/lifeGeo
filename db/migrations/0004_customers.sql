-- Apply once after 0003_sessions_devices.sql.
-- PostGIS extension must be available.
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
    
    -- Enforce explicit no_location state when coordinates are absent
    CONSTRAINT chk_customer_location_status CHECK (
        (geom IS NULL AND status = 'no_location') OR 
        (geom IS NOT NULL AND status != 'no_location')
    )
);

-- Spatial index for radius calculations
CREATE INDEX customers_geom_idx ON customers USING GIST (geom);
-- Index for tenant-scoped lookups
CREATE INDEX customers_tenant_idx ON customers (tenant_id);

-- Enforce tenant isolation via Row Level Security (RLS)
ALTER TABLE customers ENABLE ROW LEVEL SECURITY;
-- Table owners normally bypass RLS; force the policy for the owner as well.
ALTER TABLE customers FORCE ROW LEVEL SECURITY;

-- Policy requires the API to set app.current_tenant for each transaction.
CREATE POLICY tenant_isolation_policy ON customers
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

/*
NOTE ON API INTEGRATION:
Before accessing the `customers` table, Infrastructure must begin a transaction
and set the authenticated tenant ID with a parameterized call such as:

    SELECT set_config('app.current_tenant', @tenant_id, true);

The third argument makes the setting transaction-local. Use the same transaction
for all customer queries and writes. Missing tenant context denies access. The
runtime database role must not be a superuser or have BYPASSRLS; those roles can
bypass policies even when FORCE ROW LEVEL SECURITY is enabled.
*/
