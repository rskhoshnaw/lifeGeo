-- Regions and teams (plan step 0.6a, product decision D11).
-- Each team operates in exactly one region. The sales manager defines regions
-- and assigns them to teams. A user belongs to at most one team.
CREATE TABLE regions (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    name text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, name)
);

CREATE TABLE teams (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    region_id uuid NOT NULL,
    name text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, name),
    FOREIGN KEY (tenant_id, region_id) REFERENCES regions(tenant_id, id)
);

CREATE TABLE team_members (
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    team_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, user_id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, team_id) REFERENCES teams(tenant_id, id)
);

CREATE INDEX teams_tenant_region_idx ON teams (tenant_id, region_id);
CREATE INDEX team_members_tenant_team_idx ON team_members (tenant_id, team_id);

-- Customers get a structured region. The older free-text "region" column is kept unchanged.
ALTER TABLE customers ADD COLUMN region_id uuid;
ALTER TABLE customers ADD CONSTRAINT customers_region_fk
    FOREIGN KEY (tenant_id, region_id) REFERENCES regions(tenant_id, id);
CREATE INDEX customers_tenant_region_idx ON customers (tenant_id, region_id);

ALTER TABLE regions ENABLE ROW LEVEL SECURITY;
ALTER TABLE regions FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON regions
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE teams ENABLE ROW LEVEL SECURITY;
ALTER TABLE teams FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON teams
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE team_members ENABLE ROW LEVEL SECURITY;
ALTER TABLE team_members FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON team_members
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
