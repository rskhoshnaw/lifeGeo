-- Marketing messages (plan step 5.7, owner request 2026-10-26). Free way: the app builds a queue (one row per customer with a phone), the user opens each chat with one tap (wa.me) and
-- presses "sent" himself; nothing is sent by the server and no unofficial automation exists. A template is a text with {name}; a campaign copies the text, picks the customers inside the
-- creator's data scope and writes the message of every customer once (the same phone only once). marketing.use (sales manager, supervisor, sales rep, scientific rep) creates campaigns
-- and works the queue of his own campaigns (the tenant-wide scope sees all); marketing.manage (sales manager) manages the templates.
CREATE TABLE marketing_templates (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    body text NOT NULL CHECK (length(body) BETWEEN 1 AND 1000),
    is_active boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);

CREATE TABLE marketing_campaigns (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    body text NOT NULL CHECK (length(body) BETWEEN 1 AND 1000),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX marketing_campaigns_creator_idx ON marketing_campaigns (tenant_id, created_by, created_at DESC);

CREATE TABLE marketing_messages (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    campaign_id uuid NOT NULL,
    customer_id uuid NOT NULL,
    phone text NOT NULL,
    message text NOT NULL,
    status text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'sent', 'skipped')),
    handled_at timestamptz,
    handled_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK ((status = 'queued') = (handled_at IS NULL)),
    UNIQUE (tenant_id, campaign_id, customer_id),
    FOREIGN KEY (tenant_id, campaign_id) REFERENCES marketing_campaigns(tenant_id, id),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, handled_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX marketing_messages_campaign_idx ON marketing_messages (tenant_id, campaign_id, status);

ALTER TABLE marketing_templates ENABLE ROW LEVEL SECURITY;
ALTER TABLE marketing_templates FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON marketing_templates FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE marketing_campaigns ENABLE ROW LEVEL SECURITY;
ALTER TABLE marketing_campaigns FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON marketing_campaigns FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE marketing_messages ENABLE ROW LEVEL SECURITY;
ALTER TABLE marketing_messages FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON marketing_messages FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

CREATE OR REPLACE FUNCTION seed_default_roles(p_tenant uuid) RETURNS void LANGUAGE sql AS $$
    WITH defaults(name, data_scope, requires_device_binding) AS (
        VALUES ('CompanyManager', 'tenant', false),
               ('SalesManager', 'tenant', false),
               ('Supervisor', 'region', false),
               ('FinanceManager', 'tenant', false),
               ('SalesRep', 'region', true),
               ('ScientificRep', 'own', true),
               ('WarehouseManager', 'own', false),
               ('Driver', 'own', true),
               ('WholesalerOfficer', 'own', true)
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
               ('WarehouseManager', 'warehouse.work'),
               ('Driver', 'delivery.work'),
               ('FinanceManager', 'payments.record'), ('FinanceManager', 'payments.manage'), ('FinanceManager', 'payments.read'),
               ('SalesRep', 'payments.record'), ('CompanyManager', 'payments.read'), ('SalesManager', 'payments.read'), ('Supervisor', 'payments.read'),
               ('ScientificRep', 'samples.record'), ('SalesRep', 'samples.record'), ('SalesManager', 'samples.read'), ('SalesManager', 'samples.manage'),
               ('SalesRep', 'followups.create'), ('ScientificRep', 'followups.create'), ('SalesManager', 'followups.create'), ('Supervisor', 'followups.create'),
               ('SalesManager', 'followups.manage'), ('Supervisor', 'followups.manage'),
               ('SalesManager', 'followups.read'), ('Supervisor', 'followups.read'), ('CompanyManager', 'followups.read'),
               ('SalesManager', 'alerts.read'), ('Supervisor', 'alerts.read'), ('CompanyManager', 'alerts.read'),
               ('SalesManager', 'alerts.security.read'), ('CompanyManager', 'alerts.security.read'),
               ('SalesManager', 'alerts.handle'), ('Supervisor', 'alerts.handle'),
               ('CompanyManager', 'reports.read'), ('SalesManager', 'reports.read'), ('Supervisor', 'reports.read'),
               ('CompanyManager', 'samples.read'), ('Supervisor', 'samples.read'),
               ('SalesManager', 'orders.create'), ('SalesManager', 'orders.read'), ('SalesManager', 'orders.cancel'), ('SalesManager', 'warehouses.manage'),
               ('Supervisor', 'orders.create'), ('Supervisor', 'orders.read'), ('Supervisor', 'orders.cancel'),
               ('SalesRep', 'orders.create'), ('SalesRep', 'orders.read'),
               ('FinanceManager', 'orders.read'), ('CompanyManager', 'orders.read'),
               ('SalesManager', 'catalog.read'), ('SalesManager', 'catalog.manage'), ('Supervisor', 'catalog.read'), ('FinanceManager', 'catalog.read'), ('CompanyManager', 'catalog.read'),
               ('SalesRep', 'catalog.read'), ('ScientificRep', 'catalog.read'),
               ('SalesRep', 'customers.read'), ('SalesRep', 'customers.write'), ('SalesRep', 'customers.location.write'), ('SalesRep', 'visits.fail'), ('SalesRep', 'visits.create_unplanned'), ('SalesRep', 'visits.plan'),
               ('ScientificRep', 'customers.read'), ('ScientificRep', 'visits.fail'), ('ScientificRep', 'visits.create_unplanned'), ('ScientificRep', 'visits.plan'),
               ('SalesManager', 'forms.manage'), ('CompanyManager', 'forms.manage'),
               ('SalesManager', 'samples.record'),
               ('SalesManager', 'notifications.send'), ('Supervisor', 'notifications.send'),
               ('CompanyManager', 'customers.doctors.read'), ('SalesManager', 'customers.doctors.read'), ('Supervisor', 'customers.doctors.read'), ('FinanceManager', 'customers.doctors.read'), ('ScientificRep', 'customers.doctors.read'),
               ('ScientificRep', 'customers.write'), ('ScientificRep', 'customers.location.write'), ('ScientificRep', 'customers.doctors.only'),
               ('SalesRep', 'catalog.prices.restricted'), ('ScientificRep', 'catalog.prices.restricted'),
               ('SalesManager', 'targets.manage'), ('SalesManager', 'targets.read'), ('Supervisor', 'targets.manage'), ('Supervisor', 'targets.read'),
               ('CompanyManager', 'targets.read'), ('FinanceManager', 'targets.read'),
               ('WholesalerOfficer', 'wholesaler.work'),
               ('SalesManager', 'marketing.use'), ('Supervisor', 'marketing.use'), ('SalesRep', 'marketing.use'), ('ScientificRep', 'marketing.use'),
               ('SalesManager', 'marketing.manage')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

-- The permissions for the companies that exist already.
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, g.permission_key
FROM roles r
JOIN (VALUES ('SalesManager', 'marketing.use'), ('Supervisor', 'marketing.use'), ('SalesRep', 'marketing.use'), ('ScientificRep', 'marketing.use'),
             ('SalesManager', 'marketing.manage')) AS g(role_name, permission_key) ON g.role_name = r.name
ON CONFLICT DO NOTHING;
