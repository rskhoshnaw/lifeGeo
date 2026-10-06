-- Warehouse work (plan step 2.3, owner decisions 2026-10-07).
--   warehouse_staff : which users work in which warehouse (set by the sales manager). A warehouse worker sees and handles only the
--                     orders of the warehouses they belong to.
--   orders.status   : placed -> preparing -> ready -> handed_over (cancelled stays possible before the hand-over; delivery comes with 2.4).
--   order_items     : prepared_quantity (what the warehouse really prepared, 0 up to the ordered quantity) and shortage_reason (needed when less
--                     was prepared). The order total is not changed; finance decides about a shortage later.
--   order_status_history : who moved an order to which status and when (also in the audit log).
--   orders.driver_id : the driver who took the order, chosen by the warehouse worker at the hand-over.
-- Permission warehouse.work (WarehouseManager role). The panel and the app both use it, so the WarehouseManager role no longer requires an approved
-- phone (owner decision 2026-10-07: warehouse staff work at the panel and in the app); the Driver role still does.
UPDATE roles SET requires_device_binding = false WHERE name = 'WarehouseManager';
CREATE TABLE warehouse_staff (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    warehouse_id uuid NOT NULL,
    user_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, warehouse_id, user_id),
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX warehouse_staff_user_idx ON warehouse_staff (tenant_id, user_id);

ALTER TABLE orders DROP CONSTRAINT orders_status_check;
ALTER TABLE orders ADD CONSTRAINT orders_status_check CHECK (status IN ('placed', 'preparing', 'ready', 'handed_over', 'cancelled'));
ALTER TABLE orders
    ADD COLUMN driver_id uuid,
    ADD CONSTRAINT orders_driver_fk FOREIGN KEY (tenant_id, driver_id) REFERENCES users(tenant_id, id),
    ADD CONSTRAINT orders_driver_when_handed_over CHECK ((status = 'handed_over') = (driver_id IS NOT NULL) OR status = 'cancelled');

ALTER TABLE order_items
    ADD COLUMN prepared_quantity numeric(18, 3) CHECK (prepared_quantity >= 0),
    ADD COLUMN shortage_reason text CHECK (char_length(shortage_reason) <= 300),
    ADD CONSTRAINT order_items_prepared_not_more CHECK (prepared_quantity IS NULL OR prepared_quantity <= quantity),
    ADD CONSTRAINT order_items_shortage_needs_reason CHECK (prepared_quantity IS NULL OR prepared_quantity = quantity OR shortage_reason IS NOT NULL);

CREATE TABLE order_status_history (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    order_id uuid NOT NULL,
    status text NOT NULL,
    actor_id uuid NOT NULL,
    note text CHECK (char_length(note) <= 500),
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, order_id) REFERENCES orders(tenant_id, id),
    FOREIGN KEY (tenant_id, actor_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX order_status_history_order_idx ON order_status_history (tenant_id, order_id, occurred_at);

-- The orders that already exist get their first history row.
INSERT INTO order_status_history (tenant_id, order_id, status, actor_id, occurred_at)
SELECT tenant_id, id, 'placed', created_by, created_at FROM orders;
INSERT INTO order_status_history (tenant_id, order_id, status, actor_id, note, occurred_at)
SELECT tenant_id, id, 'cancelled', cancelled_by, cancel_reason, cancelled_at FROM orders WHERE status = 'cancelled' AND cancelled_by IS NOT NULL;

ALTER TABLE warehouse_staff ENABLE ROW LEVEL SECURITY;
ALTER TABLE warehouse_staff FORCE ROW LEVEL SECURITY;
ALTER TABLE order_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON warehouse_staff FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON order_status_history FOR ALL
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

-- Existing companies get the same permission.
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'warehouse.work' FROM roles r WHERE r.name = 'WarehouseManager'
ON CONFLICT DO NOTHING;
