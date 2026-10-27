-- Plan templates (plan step 5.8): a week of visits saved as a pattern for one field user (weekday + customer) and applied to the coming weeks. Applying creates the visits through the
-- normal plan generation (scope, accepted hours, "no second visit the same day" all still apply); the template itself changes nothing until it is applied.
CREATE TABLE plan_templates (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX plan_templates_user_idx ON plan_templates (tenant_id, user_id, created_at DESC);

CREATE TABLE plan_template_items (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    template_id uuid NOT NULL,
    weekday integer NOT NULL CHECK (weekday BETWEEN 0 AND 6),
    customer_id uuid NOT NULL,
    position integer NOT NULL,
    PRIMARY KEY (tenant_id, template_id, weekday, customer_id),
    FOREIGN KEY (tenant_id, template_id) REFERENCES plan_templates(tenant_id, id) ON DELETE CASCADE,
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id)
);

ALTER TABLE plan_templates ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_templates FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON plan_templates FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE plan_template_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_template_items FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON plan_template_items FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
