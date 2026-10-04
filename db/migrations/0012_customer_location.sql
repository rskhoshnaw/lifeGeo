-- Customer location and geofence radius (plan step 1.5, PRD §6.1, §8).
-- geom/location_source/location_updated_at/location_updated_by already exist (0004).
-- geofence_radius_m NULL means the default radius of 20 m.
ALTER TABLE customers
    ADD COLUMN location_accuracy_m numeric(7,2) CHECK (location_accuracy_m > 0 AND location_accuracy_m <= 100),
    ADD COLUMN geofence_radius_m integer CHECK (geofence_radius_m BETWEEN 5 AND 1000),
    ADD CONSTRAINT customers_location_source_check
        CHECK (location_source IS NULL OR location_source IN ('gps', 'manual'));


-- customers.location.write: set or correct a location (managers, supervisors, field reps for customers in scope).
-- customers.geofence.write: change a customer's check-in radius (managers, supervisors).
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
               ('SalesManager', 'customers.read'), ('SalesManager', 'customers.write'), ('SalesManager', 'customers.location.write'), ('SalesManager', 'customers.geofence.write'), ('SalesManager', 'visits.write'), ('SalesManager', 'visits.cancel'),
               ('SalesManager', 'regions.read'), ('SalesManager', 'regions.manage'),
               ('SalesManager', 'teams.read'), ('SalesManager', 'teams.manage'),
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'customers.location.write'), ('Supervisor', 'customers.geofence.write'), ('Supervisor', 'visits.write'), ('Supervisor', 'visits.cancel'),
               ('Supervisor', 'regions.read'), ('Supervisor', 'teams.read'),
               ('FinanceManager', 'customers.read'), ('FinanceManager', 'audit.read'),
               ('SalesRep', 'customers.read'), ('SalesRep', 'customers.write'), ('SalesRep', 'customers.location.write'), ('SalesRep', 'visits.fail'), ('SalesRep', 'visits.create_unplanned'),
               ('ScientificRep', 'customers.read'), ('ScientificRep', 'visits.fail'), ('ScientificRep', 'visits.create_unplanned')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, p.key
FROM roles r
JOIN (VALUES ('SalesManager', 'customers.location.write'), ('SalesManager', 'customers.geofence.write'),
             ('Supervisor', 'customers.location.write'), ('Supervisor', 'customers.geofence.write'),
             ('SalesRep', 'customers.location.write')) AS p(role_name, key) ON p.role_name = r.name
ON CONFLICT DO NOTHING;
