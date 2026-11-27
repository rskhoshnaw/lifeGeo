-- Plan step 5.67 (owner decision 2026-11-27): every province has one warehouse and the cities of the province order from it.
--  * city_warehouses: for each city the warehouse that serves it (a city has at most one; a warehouse serves many cities). Company warehouses only; a wholesaler stays chosen as in 5.59.
--    The cities that already stood on a company warehouse (warehouses.city_id) are copied (the first warehouse by code when two stand on one city).
--  * orders.anywarehouse: may choose another company warehouse than the one of the customer's city (the sales manager). Everyone else gets the warehouse of the customer's city.
CREATE TABLE city_warehouses (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    city_id uuid NOT NULL,
    warehouse_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, city_id),
    FOREIGN KEY (tenant_id, city_id) REFERENCES places(tenant_id, id),
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES warehouses(tenant_id, id)
);
CREATE INDEX city_warehouses_warehouse_idx ON city_warehouses (tenant_id, warehouse_id);
ALTER TABLE city_warehouses ENABLE ROW LEVEL SECURITY;
ALTER TABLE city_warehouses FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON city_warehouses FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

INSERT INTO city_warehouses (tenant_id, city_id, warehouse_id)
SELECT DISTINCT ON (w.tenant_id, w.city_id) w.tenant_id, w.city_id, w.id
FROM warehouses w
WHERE w.city_id IS NOT NULL AND w.kind = 'company' AND w.status = 'active'
ORDER BY w.tenant_id, w.city_id, w.code COLLATE "C"
ON CONFLICT DO NOTHING;

-- seed_default_roles copied from 0079 with orders.anywarehouse added.
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
               ('WholesalerOfficer', 'own', true),
               ('TvDisplay', 'tenant', false)
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
               ('CompanyManager', 'client_errors.read'), ('SalesManager', 'client_errors.read'),
               ('CompanyManager', 'feedback.manage'), ('SalesManager', 'feedback.manage'), ('Supervisor', 'feedback.manage'),
               ('SalesRep', 'returns.create'), ('SalesManager', 'returns.create'), ('Supervisor', 'returns.create'),
               ('SalesManager', 'returns.read'), ('CompanyManager', 'returns.read'), ('FinanceManager', 'returns.read'), ('Supervisor', 'returns.read'),
               ('SalesManager', 'returns.approve'),
               ('SalesManager', 'orders.anywarehouse'),
               ('TvDisplay', 'tracking.read')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;


-- The permission for the companies that exist already.
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'orders.anywarehouse' FROM roles r WHERE r.name = 'SalesManager'
ON CONFLICT DO NOTHING;
