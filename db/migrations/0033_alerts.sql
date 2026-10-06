-- Alerts in the panel (plan step 2.9, owner decisions 2026-10-07).
--   An alert says that something needs a manager's attention: source (kind + the thing it is about), time, severity, status and who may see it.
--   Two categories, kept apart by permission: "operational" (alerts.read) and "security" (alerts.security.read).
--   Kinds now: operational: followup_overdue, visit_delayed (a planned visit with a planned time did not start 30 minutes after it), visit_long_stay
--   (in a visit for 90 minutes; the detail says whether the report is saved), tracking_gap (an open shift with no point for 15 minutes);
--   security: new_device (a phone waits for approval), gps_mocked (a position the phone says is fake). More security kinds come with 2.10.
--   No flood: one alert for one event (dedupe_key is unique), so a scan that runs again never makes a second alert for the same event,
--   even after somebody has handled it. A scan runs in the background every few minutes and on demand (POST /alerts/scan).
--   Status: new -> seen -> handled (a note is needed). Audience: the managers whose area covers the user the alert is about (region = the region of
--   the user's team; tenant-wide readers see all, so the sales manager covers a region without a supervisor).
-- Permissions: alerts.read (SalesManager, CompanyManager: all; Supervisor: region), alerts.security.read (SalesManager, CompanyManager),
-- alerts.handle (SalesManager, Supervisor; handling a security alert also needs alerts.security.read).
CREATE TABLE alerts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    kind text NOT NULL CHECK (kind IN ('followup_overdue', 'visit_delayed', 'visit_long_stay', 'tracking_gap', 'new_device', 'gps_mocked')),
    category text NOT NULL CHECK (category IN ('operational', 'security')),
    severity text NOT NULL CHECK (severity IN ('info', 'warning', 'critical')),
    subject_user_id uuid,
    region_id uuid,
    customer_id uuid,
    entity_id text NOT NULL,
    detail text CHECK (char_length(detail) <= 200),
    dedupe_key text NOT NULL CHECK (char_length(dedupe_key) BETWEEN 1 AND 200),
    status text NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'seen', 'handled')),
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    seen_at timestamptz,
    seen_by uuid,
    handled_at timestamptz,
    handled_by uuid,
    handled_note text CHECK (char_length(handled_note) BETWEEN 1 AND 500),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, dedupe_key),
    CONSTRAINT alerts_handled_fields CHECK ((status = 'handled') = (handled_at IS NOT NULL AND handled_by IS NOT NULL AND handled_note IS NOT NULL)),
    FOREIGN KEY (tenant_id, subject_user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, seen_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, handled_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX alerts_open_idx ON alerts (tenant_id, category, status, occurred_at DESC);
CREATE INDEX alerts_region_idx ON alerts (tenant_id, region_id) WHERE region_id IS NOT NULL;

ALTER TABLE alerts ENABLE ROW LEVEL SECURITY;
ALTER TABLE alerts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON alerts FOR ALL
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
               ('CompanyManager', 'samples.read'), ('Supervisor', 'samples.read'),
               ('SalesManager', 'orders.create'), ('SalesManager', 'orders.read'), ('SalesManager', 'orders.cancel'), ('SalesManager', 'warehouses.manage'),
               ('Supervisor', 'orders.create'), ('Supervisor', 'orders.read'), ('Supervisor', 'orders.cancel'),
               ('SalesRep', 'orders.create'), ('SalesRep', 'orders.read'),
               ('FinanceManager', 'orders.read'), ('CompanyManager', 'orders.read'),
               ('SalesManager', 'catalog.read'), ('SalesManager', 'catalog.manage'), ('Supervisor', 'catalog.read'), ('FinanceManager', 'catalog.read'), ('CompanyManager', 'catalog.read'),
               ('SalesRep', 'catalog.read'), ('ScientificRep', 'catalog.read'),
               ('SalesRep', 'customers.read'), ('SalesRep', 'customers.write'), ('SalesRep', 'customers.location.write'), ('SalesRep', 'visits.fail'), ('SalesRep', 'visits.create_unplanned'), ('SalesRep', 'visits.plan'),
               ('ScientificRep', 'customers.read'), ('ScientificRep', 'visits.fail'), ('ScientificRep', 'visits.create_unplanned'), ('ScientificRep', 'visits.plan')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

-- Existing companies get the same permissions.
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, g.permission_key
FROM (VALUES ('SalesManager', 'alerts.read'), ('Supervisor', 'alerts.read'), ('CompanyManager', 'alerts.read'),
             ('SalesManager', 'alerts.security.read'), ('CompanyManager', 'alerts.security.read'),
             ('SalesManager', 'alerts.handle'), ('Supervisor', 'alerts.handle')) AS g(role_name, permission_key)
JOIN roles r ON r.name = g.role_name
ON CONFLICT DO NOTHING;
