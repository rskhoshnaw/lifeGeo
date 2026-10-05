-- Flexible visit planning, part 1 (plan step 1.23, owner request 2026-10-05).
--   visits.sort_order       - position of a visit in its assignee's plan for the day (NULL = not ordered yet; such
--                             visits are listed after the ordered ones).
--   customer_visit_windows  - weekly days and hours in which a customer accepts visits (several per day allowed);
--                             a customer without windows has no restriction. weekday: 0 = Sunday .. 6 = Saturday.
--   visits.plan             - new permission: a field user reorders and postpones visits of their own plan.
--                             Managers and supervisors use visits.write (data scope applies).
-- Earlier migrations are not edited; seed_default_roles is replaced with the 0019 version plus the new grant.
--   visits.postpone_count / postpone_reason - how often a visit was moved to another day and the last reason
--                             (kept for later reports; the audit log has one row per move).
ALTER TABLE visits ADD COLUMN sort_order integer CHECK (sort_order IS NULL OR sort_order > 0);
ALTER TABLE visits ADD COLUMN postpone_count integer NOT NULL DEFAULT 0 CHECK (postpone_count >= 0);
ALTER TABLE visits ADD COLUMN postpone_reason text CHECK (postpone_reason IS NULL OR char_length(postpone_reason) <= 500);

CREATE TABLE customer_visit_windows (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    customer_id uuid NOT NULL,
    weekday smallint NOT NULL CHECK (weekday BETWEEN 0 AND 6),
    opens_at time NOT NULL,
    closes_at time NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id) ON DELETE CASCADE,
    CONSTRAINT chk_visit_window_order CHECK (opens_at < closes_at),
    UNIQUE (customer_id, weekday, opens_at)
);
CREATE INDEX customer_visit_windows_customer_idx ON customer_visit_windows (tenant_id, customer_id);

ALTER TABLE customer_visit_windows ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_visit_windows FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON customer_visit_windows
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
               ('SalesManager', 'tracking.read'),
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'customers.location.write'), ('Supervisor', 'customers.geofence.write'), ('Supervisor', 'visits.write'), ('Supervisor', 'visits.cancel'), ('Supervisor', 'customers.approve'),
               ('Supervisor', 'regions.read'), ('Supervisor', 'teams.read'), ('Supervisor', 'tracking.read'),
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
SELECT r.tenant_id, r.id, 'visits.plan' FROM roles r WHERE r.name IN ('SalesRep', 'ScientificRep')
ON CONFLICT DO NOTHING;
