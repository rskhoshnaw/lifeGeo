-- Visit planning fields (plan step 1.2, PRD §5.1).
-- visit_type and priority are versioned text contracts shared with mobile; planned_time is the
-- local time of day in the tenant's time zone (Asia/Baghdad for Iraq); NULL means "any time that day".
-- An unplanned visit is created by the field user on the spot and must carry a reason.
ALTER TABLE visits
    ADD COLUMN visit_type text NOT NULL DEFAULT 'other'
        CHECK (visit_type IN ('pharmacy', 'doctor', 'wholesaler', 'other')),
    ADD COLUMN priority text NOT NULL DEFAULT 'normal'
        CHECK (priority IN ('high', 'normal', 'low')),
    ADD COLUMN planned_time time,
    ADD COLUMN is_unplanned boolean NOT NULL DEFAULT false,
    ADD COLUMN unplanned_reason text,
    ADD CONSTRAINT visits_unplanned_reason_check
        CHECK (NOT is_unplanned OR length(btrim(coalesce(unplanned_reason, ''))) BETWEEN 1 AND 500);

-- Several visits per customer per day are allowed (PRD §5.1); this index serves the daily plan.
CREATE INDEX visits_tenant_user_date_idx ON visits (tenant_id, user_id, schedule_date);

-- Field roles may record unplanned visits (visits.create_unplanned). 0009 is not edited because it may
-- already be applied; the seed function is replaced and existing default rep roles get the grant.
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
               ('SalesManager', 'customers.read'), ('SalesManager', 'customers.write'), ('SalesManager', 'visits.write'),
               ('SalesManager', 'regions.read'), ('SalesManager', 'regions.manage'),
               ('SalesManager', 'teams.read'), ('SalesManager', 'teams.manage'),
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'visits.write'),
               ('Supervisor', 'regions.read'), ('Supervisor', 'teams.read'),
               ('FinanceManager', 'customers.read'), ('FinanceManager', 'audit.read'),
               ('SalesRep', 'customers.read'), ('SalesRep', 'customers.write'), ('SalesRep', 'visits.fail'), ('SalesRep', 'visits.create_unplanned'),
               ('ScientificRep', 'customers.read'), ('ScientificRep', 'visits.fail'), ('ScientificRep', 'visits.create_unplanned')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'visits.create_unplanned' FROM roles r WHERE r.name IN ('SalesRep', 'ScientificRep')
ON CONFLICT DO NOTHING;
