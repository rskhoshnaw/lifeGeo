CREATE TABLE visits (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    customer_id uuid NOT NULL,
    status text NOT NULL,
    exception_reason text,
    schedule_date date NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),

    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id)
);

CREATE INDEX visits_tenant_idx ON visits (tenant_id);
CREATE INDEX visits_tenant_user_idx ON visits (tenant_id, user_id);
CREATE INDEX visits_tenant_customer_idx ON visits (tenant_id, customer_id);

ALTER TABLE visits ENABLE ROW LEVEL SECURITY;
ALTER TABLE visits FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation_policy ON visits
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
