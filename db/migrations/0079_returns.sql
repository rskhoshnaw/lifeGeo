-- Customer returns (plan step 5.54, owner decisions 2026-11-26/27).
--   A return is made for a customer (optionally against one delivered order) with one line per product: quantity, reason (damaged, expired, wrong_item, customer_request, other) and where the
--   goods go: 'warehouse' (back to the warehouse stock) or 'damaged' (damaged or expired goods, counted apart: recorded only, never added to the stock that can be sold).
--   The credit is the unit price of the order line (or today's price of the customer without an order) x quantity; one currency per return. It reduces the customer's balance only when approved.
--   Status: requested -> approved | rejected (the sales manager decides; a rejection needs a reason) | cancelled (by its author while it is requested). Approval puts the 'warehouse' lines back on the
--   stock of the chosen warehouse (only where that warehouse already counts the product: else stock_applied stays false and nothing is added, so an uncounted product does not become limited).
--   Every change is in the audit log; the sales managers are told of a new return and the author of the decision (notification kinds return.new and return.decided).
-- Permissions: returns.create (sales rep, sales manager, supervisor), returns.read (sales manager, company manager, finance manager: all; supervisor: the region), returns.approve (sales manager).
CREATE TABLE return_counters (
    tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
    last_number bigint NOT NULL DEFAULT 0
);

CREATE TABLE returns (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    number bigint NOT NULL,
    customer_id uuid NOT NULL,
    order_id uuid,
    warehouse_id uuid,
    created_by uuid NOT NULL,
    status text NOT NULL DEFAULT 'requested' CHECK (status IN ('requested', 'approved', 'rejected', 'cancelled')),
    currency text NOT NULL CHECK (currency IN ('IQD', 'USD')),
    total numeric(18, 3) NOT NULL CHECK (total >= 0),
    note text CHECK (char_length(note) <= 500),
    client_ref uuid NOT NULL,
    decided_by uuid,
    decided_at timestamptz,
    decision_note text CHECK (char_length(decision_note) <= 500),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, number),
    UNIQUE (tenant_id, client_ref),
    CONSTRAINT returns_decision_fields CHECK ((status = 'requested') = (decided_at IS NULL) OR status = 'cancelled'),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, order_id) REFERENCES orders(tenant_id, id),
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, decided_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX returns_customer_idx ON returns (tenant_id, customer_id, created_at DESC);
CREATE INDEX returns_created_idx ON returns (tenant_id, created_at DESC, id DESC);
CREATE INDEX returns_author_idx ON returns (tenant_id, created_by, created_at DESC);
CREATE INDEX returns_order_idx ON returns (tenant_id, order_id) WHERE order_id IS NOT NULL;

CREATE TABLE return_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    return_id uuid NOT NULL,
    line_no integer NOT NULL CHECK (line_no > 0),
    product_id uuid NOT NULL,
    product_code text NOT NULL,
    product_name text NOT NULL,
    unit text NOT NULL,
    quantity numeric(18, 3) NOT NULL CHECK (quantity > 0),
    unit_price numeric(18, 3) NOT NULL CHECK (unit_price >= 0),
    line_total numeric(18, 3) NOT NULL CHECK (line_total >= 0),
    reason text NOT NULL CHECK (reason IN ('damaged', 'expired', 'wrong_item', 'customer_request', 'other')),
    destination text NOT NULL CHECK (destination IN ('warehouse', 'damaged')),
    stock_applied boolean,
    UNIQUE (return_id, line_no),
    UNIQUE (return_id, product_id),
    FOREIGN KEY (tenant_id, return_id) REFERENCES returns(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id)
);

ALTER TABLE return_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE return_counters FORCE ROW LEVEL SECURITY;
ALTER TABLE returns ENABLE ROW LEVEL SECURITY;
ALTER TABLE returns FORCE ROW LEVEL SECURITY;
ALTER TABLE return_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE return_items FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON return_counters FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON returns FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON return_items FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check
    CHECK (kind IN ('customer.profile_changed', 'customer.location_changed', 'manager.message', 'tracking.stopped', 'feedback.new', 'feedback.reply', 'customer.owner_changed', 'team.shift_stopped', 'return.new', 'return.decided'));

-- seed_default_roles copied from 0070 with the three return permissions added.
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
               ('TvDisplay', 'tracking.read')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

-- The permissions for the companies that exist already.
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, g.permission_key
FROM roles r
JOIN (VALUES ('SalesRep', 'returns.create'), ('SalesManager', 'returns.create'), ('Supervisor', 'returns.create'),
             ('SalesManager', 'returns.read'), ('CompanyManager', 'returns.read'), ('FinanceManager', 'returns.read'), ('Supervisor', 'returns.read'),
             ('SalesManager', 'returns.approve')) AS g(role_name, permission_key) ON g.role_name = r.name
ON CONFLICT DO NOTHING;
