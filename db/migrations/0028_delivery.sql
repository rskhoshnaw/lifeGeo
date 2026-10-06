-- Delivery by the driver (plan step 2.4, owner decisions 2026-10-07).
--   orders.status : handed_over -> picked_up -> on_the_way -> delivered | delivery_failed. The driver took the goods from the warehouse
--                   (picked_up), left (on_the_way) and then handed them over (delivered) or could not (delivery_failed, with a reason). A failed
--                   delivery is final for now (the warehouse and the sellers decide what happens next).
--   Evidence      : every step of the driver stores the GPS position the phone sent (latitude, longitude, accuracy) and, when both are known, the
--                   distance to the customer's position. It is evidence only: nothing is refused for being far away (a phone that says its
--                   position is fake is refused). The receiver's name and a note are stored on a delivered order.
--   order_items   : delivered_quantity (0 up to the prepared quantity; at least one line above 0) and delivery_reason (needed when less was delivered
--                   than prepared). The order total does not change; finance decides about a difference later.
-- Permission delivery.work (Driver role): a driver sees and handles only the orders assigned to them.
ALTER TABLE orders DROP CONSTRAINT orders_status_check;
ALTER TABLE orders ADD CONSTRAINT orders_status_check
    CHECK (status IN ('placed', 'preparing', 'ready', 'handed_over', 'picked_up', 'on_the_way', 'delivered', 'delivery_failed', 'cancelled'));
ALTER TABLE orders DROP CONSTRAINT orders_driver_when_handed_over;
ALTER TABLE orders ADD CONSTRAINT orders_driver_when_handed_over
    CHECK ((status IN ('handed_over', 'picked_up', 'on_the_way', 'delivered', 'delivery_failed')) = (driver_id IS NOT NULL) OR status = 'cancelled');
ALTER TABLE orders
    ADD COLUMN receiver_name text CHECK (char_length(receiver_name) <= 100),
    ADD COLUMN delivery_note text CHECK (char_length(delivery_note) <= 500),
    ADD COLUMN failure_reason text CHECK (char_length(failure_reason) <= 300),
    ADD CONSTRAINT orders_failure_reason_when_failed CHECK ((status = 'delivery_failed') = (failure_reason IS NOT NULL)),
    ADD CONSTRAINT orders_receiver_when_delivered CHECK ((status = 'delivered') = (receiver_name IS NOT NULL));

ALTER TABLE order_items
    ADD COLUMN delivered_quantity numeric(18, 3) CHECK (delivered_quantity >= 0),
    ADD COLUMN delivery_reason text CHECK (char_length(delivery_reason) <= 300),
    ADD CONSTRAINT order_items_delivered_not_more CHECK (delivered_quantity IS NULL OR delivered_quantity <= coalesce(prepared_quantity, quantity)),
    ADD CONSTRAINT order_items_delivery_needs_reason CHECK (delivered_quantity IS NULL OR delivered_quantity = coalesce(prepared_quantity, quantity) OR delivery_reason IS NOT NULL);

ALTER TABLE order_status_history
    ADD COLUMN latitude double precision CHECK (latitude BETWEEN -90 AND 90),
    ADD COLUMN longitude double precision CHECK (longitude BETWEEN -180 AND 180),
    ADD COLUMN accuracy_m double precision CHECK (accuracy_m >= 0),
    ADD COLUMN distance_m integer CHECK (distance_m >= 0);

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
SELECT r.tenant_id, r.id, 'delivery.work' FROM roles r WHERE r.name = 'Driver'
ON CONFLICT DO NOTHING;
