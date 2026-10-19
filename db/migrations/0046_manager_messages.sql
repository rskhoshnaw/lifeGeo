-- Messages from a manager to staff (plan step 4.16): a new kind of notification, 'manager.message'. The text is kept in entity_id (the column every
-- reader of notifications already passes on to the app push, the Telegram dispatcher and the panel), so the existing push and Telegram delivery
-- send it without new columns. Permission notifications.send: SalesManager (tenant scope: anybody) and Supervisor (region scope: only people of the
-- supervisor's region). New companies get it from seed_default_roles (copied from 0044 with two more rows), existing companies by the INSERT below.
ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check
    CHECK (kind IN ('customer.profile_changed', 'customer.location_changed', 'manager.message'));

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
               ('SalesManager', 'notifications.send'), ('Supervisor', 'notifications.send')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT tenant_id, id, 'notifications.send' FROM roles WHERE name IN ('SalesManager', 'Supervisor')
ON CONFLICT DO NOTHING;
