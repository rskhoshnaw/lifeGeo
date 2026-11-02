-- Connection errors of the mobile app (plan step 5.10, owner request 2026-11-01). Errors such as a DNS failure while the company code is looked up or while signing in never reach the server,
-- so the app keeps them on the phone and sends them when the connection works again. Each report has the company (the server that received it), the stage, the kind of error and what was typed
-- (company code, e-mail; never a password or token). Reports older than 40 days are deleted by the background job. client_errors.read (company manager, sales manager) lets the panel list them.
-- Audit: when the typed e-mail belongs to a user of the company, the report writes client_error.reported with that user as actor (audit_logs needs a real user).
CREATE TABLE client_error_reports (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    reported_at timestamptz NOT NULL DEFAULT now(),
    occurred_at timestamptz NOT NULL,
    stage text NOT NULL CHECK (stage IN ('company_lookup', 'device_register', 'login', 'other')),
    error_kind text NOT NULL CHECK (error_kind IN ('dns', 'timeout', 'tls', 'http', 'other')),
    error_type text NOT NULL CHECK (length(error_type) <= 100),
    message text NOT NULL CHECK (length(message) <= 300),
    entered_company_code text CHECK (length(entered_company_code) <= 60),
    entered_email text CHECK (length(entered_email) <= 200),
    device_id text CHECK (length(device_id) <= 100),
    app_version text CHECK (length(app_version) <= 40),
    platform text CHECK (length(platform) <= 40)
);
CREATE INDEX client_error_reports_tenant_idx ON client_error_reports (tenant_id, reported_at DESC, id);

ALTER TABLE client_error_reports ENABLE ROW LEVEL SECURITY;
ALTER TABLE client_error_reports FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON client_error_reports
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

-- seed_default_roles copied from 0060 with the new permission added.
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
               ('SalesManager', 'marketing.manage'),
               ('CompanyManager', 'client_errors.read'), ('SalesManager', 'client_errors.read')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

-- The permission for the companies that exist already.
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, g.permission_key
FROM roles r
JOIN (VALUES ('CompanyManager', 'client_errors.read'), ('SalesManager', 'client_errors.read')) AS g(role_name, permission_key) ON g.role_name = r.name
ON CONFLICT DO NOTHING;
