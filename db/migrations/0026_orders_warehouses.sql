-- Orders and warehouses (plan step 2.2, owner decisions 2026-10-07).
--   warehouses  : a place orders are sent to (code, name, active / inactive). Managed by the sales manager. The warehouse manager
--                 and delivery steps come with 2.3 / 2.4.
--   orders      : placed from the rep's app or the panel, for one approved customer, optionally linked to a visit, for one
--                 warehouse CHOSEN BY THE VISITOR. All lines of an order share one currency (IQD or USD, no conversion).
--                 Status: placed | cancelled (later steps add prepared / delivered). The prices are worked out by the server
--                 (general or city price, then the customer's discounts) and stored on each line with the discounts that were
--                 applied, so a later price change never rewrites an order. client_ref makes a retry from a phone harmless.
--   order_items : product code, name and unit are copied at that moment; quantity > 0; one line per product.
--   order_counters : one counter per company so the numbers 1, 2, 3 ... have no gaps.
-- Permissions: orders.create (sales manager, supervisor, rep), orders.read (those and finance, company manager), orders.cancel
-- (sales manager, supervisor; the creator can also cancel an order of their own that is still "placed"), warehouses.manage (sales manager).
CREATE TABLE warehouses (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    code text NOT NULL CHECK (char_length(code) BETWEEN 1 AND 40),
    name text NOT NULL CHECK (char_length(btrim(name)) BETWEEN 1 AND 160),
    status text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'inactive')),
    created_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, code),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);

CREATE TABLE order_counters (
    tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
    last_number bigint NOT NULL DEFAULT 0
);

CREATE TABLE orders (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    number bigint NOT NULL,
    customer_id uuid NOT NULL,
    visit_id uuid,
    warehouse_id uuid NOT NULL,
    created_by uuid NOT NULL,
    status text NOT NULL DEFAULT 'placed' CHECK (status IN ('placed', 'cancelled')),
    currency text NOT NULL CHECK (currency IN ('IQD', 'USD')),
    total numeric(18, 3) NOT NULL CHECK (total >= 0),
    note text CHECK (char_length(note) <= 500),
    client_ref uuid NOT NULL,
    cancel_reason text CHECK (char_length(cancel_reason) <= 500),
    cancelled_by uuid,
    cancelled_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, number),
    UNIQUE (tenant_id, client_ref),
    CONSTRAINT orders_cancel_fields CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL AND cancel_reason IS NOT NULL)),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, cancelled_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX orders_customer_idx ON orders (tenant_id, customer_id, created_at DESC);
CREATE INDEX orders_created_idx ON orders (tenant_id, created_at DESC, id DESC);
CREATE INDEX orders_visit_idx ON orders (tenant_id, visit_id) WHERE visit_id IS NOT NULL;

CREATE TABLE order_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    order_id uuid NOT NULL,
    line_no integer NOT NULL CHECK (line_no > 0),
    product_id uuid NOT NULL,
    product_code text NOT NULL,
    product_name text NOT NULL,
    unit text NOT NULL,
    quantity numeric(18, 3) NOT NULL CHECK (quantity > 0),
    list_price numeric(18, 3) NOT NULL CHECK (list_price >= 0),
    unit_price numeric(18, 3) NOT NULL CHECK (unit_price >= 0),
    line_total numeric(18, 3) NOT NULL CHECK (line_total >= 0),
    discounts jsonb NOT NULL DEFAULT '[]'::jsonb,
    UNIQUE (order_id, line_no),
    UNIQUE (order_id, product_id),
    FOREIGN KEY (tenant_id, order_id) REFERENCES orders(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id)
);

-- The visit report may now say "order placed" (the API accepts it only when the visit has a placed order).
ALTER TABLE visit_reports DROP CONSTRAINT visit_reports_outcome_check;
ALTER TABLE visit_reports ADD CONSTRAINT visit_reports_outcome_check
    CHECK (outcome IN ('products_presented', 'order_intent', 'follow_up_needed', 'no_interest', 'other', 'order_placed'));

ALTER TABLE warehouses ENABLE ROW LEVEL SECURITY;
ALTER TABLE warehouses FORCE ROW LEVEL SECURITY;
ALTER TABLE order_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_counters FORCE ROW LEVEL SECURITY;
ALTER TABLE orders ENABLE ROW LEVEL SECURITY;
ALTER TABLE orders FORCE ROW LEVEL SECURITY;
ALTER TABLE order_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE order_items FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON warehouses FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON order_counters FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON orders FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON order_items FOR ALL
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
               ('SalesManager', 'tracking.read'), ('SalesManager', 'places.manage'),
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'customers.location.write'), ('Supervisor', 'customers.geofence.write'), ('Supervisor', 'visits.write'), ('Supervisor', 'visits.cancel'), ('Supervisor', 'customers.approve'),
               ('Supervisor', 'regions.read'), ('Supervisor', 'teams.read'), ('Supervisor', 'tracking.read'), ('Supervisor', 'places.manage'),
               ('FinanceManager', 'customers.read'), ('FinanceManager', 'audit.read'), ('FinanceManager', 'tracking.summary'),
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
FROM (VALUES ('SalesManager', 'orders.create'), ('SalesManager', 'orders.read'), ('SalesManager', 'orders.cancel'), ('SalesManager', 'warehouses.manage'),
             ('Supervisor', 'orders.create'), ('Supervisor', 'orders.read'), ('Supervisor', 'orders.cancel'),
             ('SalesRep', 'orders.create'), ('SalesRep', 'orders.read'),
             ('FinanceManager', 'orders.read'), ('CompanyManager', 'orders.read')) AS g(role_name, permission_key)
JOIN roles r ON r.name = g.role_name
ON CONFLICT DO NOTHING;
