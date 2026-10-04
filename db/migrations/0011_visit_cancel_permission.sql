-- Visit cancellation by managers (plan step 1.4, decision D7): new permission visits.cancel for the
-- default SalesManager (tenant scope) and Supervisor (region scope) roles. Earlier migrations are not edited.
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
               ('SalesManager', 'customers.read'), ('SalesManager', 'customers.write'), ('SalesManager', 'visits.write'), ('SalesManager', 'visits.cancel'),
               ('SalesManager', 'regions.read'), ('SalesManager', 'regions.manage'),
               ('SalesManager', 'teams.read'), ('SalesManager', 'teams.manage'),
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'visits.write'), ('Supervisor', 'visits.cancel'),
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
SELECT r.tenant_id, r.id, 'visits.cancel' FROM roles r WHERE r.name IN ('SalesManager', 'Supervisor')
ON CONFLICT DO NOTHING;
