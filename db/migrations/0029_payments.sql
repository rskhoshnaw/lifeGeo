-- Payments (plan step 2.5, owner decisions 2026-10-07).
--   A payment belongs to a CUSTOMER and, optionally, to one of its orders (same currency). Methods: cash, FastPay, FIB (First Iraqi Bank), other;
--   FastPay and FIB need the reference (the transaction number). One currency per payment (IQD or USD, no conversion).
--   Who records: the finance manager (the payment is "confirmed" at once) and sometimes the visitor, who takes the money and gives it to the finance
--   manager (the payment is "collected" until the finance manager confirms that he received it).
--   Corrections never rewrite: a payment is VOIDED (with a reason) or CORRECTED, which writes a new payment that points at the old one
--   (replaces_payment_id) and marks the old one "superseded". Every change is in payment_history and the audit log.
--   A customer's balance (GET /customers/{id}/balance, per currency) = the value of what was delivered (delivered quantity x unit price of
--   delivered orders) minus the confirmed payments; collected payments are shown apart.
-- Permissions: payments.record (finance manager, sales rep), payments.manage (finance manager: confirm, void, correct), payments.read
-- (finance manager, company manager, sales manager: all; supervisor: the customers of the region).
CREATE TABLE payment_counters (
    tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
    last_number bigint NOT NULL DEFAULT 0
);

CREATE TABLE payments (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    number bigint NOT NULL,
    customer_id uuid NOT NULL,
    order_id uuid,
    method text NOT NULL CHECK (method IN ('cash', 'fastpay', 'fib', 'other')),
    currency text NOT NULL CHECK (currency IN ('IQD', 'USD')),
    amount numeric(18, 3) NOT NULL CHECK (amount > 0),
    reference text CHECK (char_length(reference) <= 100),
    note text CHECK (char_length(note) <= 500),
    status text NOT NULL CHECK (status IN ('collected', 'confirmed', 'voided', 'superseded')),
    received_by uuid NOT NULL,
    recorded_by uuid NOT NULL,
    confirmed_by uuid,
    confirmed_at timestamptz,
    closed_reason text CHECK (char_length(closed_reason) <= 500),
    closed_by uuid,
    closed_at timestamptz,
    replaces_payment_id uuid,
    client_ref uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, number),
    UNIQUE (tenant_id, client_ref),
    CONSTRAINT payments_reference_for_systems CHECK (method IN ('cash', 'other') OR nullif(btrim(reference), '') IS NOT NULL),
    CONSTRAINT payments_confirmed_fields CHECK ((status = 'confirmed') = (confirmed_at IS NOT NULL) OR status IN ('voided', 'superseded')),
    CONSTRAINT payments_closed_fields CHECK ((status IN ('voided', 'superseded')) = (closed_at IS NOT NULL AND closed_reason IS NOT NULL AND closed_by IS NOT NULL)),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, order_id) REFERENCES orders(tenant_id, id),
    FOREIGN KEY (tenant_id, received_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, recorded_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, confirmed_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, closed_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, replaces_payment_id) REFERENCES payments(tenant_id, id)
);
CREATE INDEX payments_customer_idx ON payments (tenant_id, customer_id, created_at DESC);
CREATE INDEX payments_created_idx ON payments (tenant_id, created_at DESC, id DESC);
CREATE INDEX payments_recorder_idx ON payments (tenant_id, recorded_by, created_at DESC);
CREATE INDEX payments_order_idx ON payments (tenant_id, order_id) WHERE order_id IS NOT NULL;

CREATE TABLE payment_history (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    payment_id uuid NOT NULL,
    status text NOT NULL,
    actor_id uuid NOT NULL,
    note text CHECK (char_length(note) <= 500),
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, payment_id) REFERENCES payments(tenant_id, id),
    FOREIGN KEY (tenant_id, actor_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX payment_history_payment_idx ON payment_history (tenant_id, payment_id, occurred_at);

ALTER TABLE payment_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment_counters FORCE ROW LEVEL SECURITY;
ALTER TABLE payments ENABLE ROW LEVEL SECURITY;
ALTER TABLE payments FORCE ROW LEVEL SECURITY;
ALTER TABLE payment_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE payment_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON payment_counters FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON payments FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON payment_history FOR ALL
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
               ('Driver', 'delivery.work'),
               ('FinanceManager', 'payments.record'), ('FinanceManager', 'payments.manage'), ('FinanceManager', 'payments.read'),
               ('SalesRep', 'payments.record'), ('CompanyManager', 'payments.read'), ('SalesManager', 'payments.read'), ('Supervisor', 'payments.read'),
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
FROM (VALUES ('FinanceManager', 'payments.record'), ('FinanceManager', 'payments.manage'), ('FinanceManager', 'payments.read'),
             ('SalesRep', 'payments.record'), ('CompanyManager', 'payments.read'), ('SalesManager', 'payments.read'), ('Supervisor', 'payments.read')) AS g(role_name, permission_key)
JOIN roles r ON r.name = g.role_name
ON CONFLICT DO NOTHING;
