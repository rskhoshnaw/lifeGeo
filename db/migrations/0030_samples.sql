-- Pharma samples (plan step 2.6, owner decisions 2026-10-07).
--   A sample distribution = a product from the catalogue (any quantity > 0), given to a customer (pharmacy/clinic), optionally during a visit.
--   Recorded: recipient name (the doctor or person who took it), batch number and expiry date (optional; an expired sample is refused), note,
--   the phone's position (optional evidence). No stock or quota control yet (later). Nothing is rewritten: a manager VOIDS a record with a reason.
--   Signature/photo are not stored yet (they need file storage). Number "S-000001", client_ref makes a retry safe.
-- Permissions: samples.record (ScientificRep, SalesRep), samples.read (CompanyManager, SalesManager: all; Supervisor: region; a recorder sees
-- their own), samples.manage (SalesManager: void).
CREATE TABLE sample_counters (
    tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
    last_number bigint NOT NULL DEFAULT 0
);

CREATE TABLE sample_distributions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    number bigint NOT NULL,
    customer_id uuid NOT NULL,
    visit_id uuid,
    product_id uuid NOT NULL,
    quantity numeric(18, 3) NOT NULL CHECK (quantity > 0),
    recipient_name text CHECK (char_length(recipient_name) BETWEEN 1 AND 100),
    batch_number text CHECK (char_length(batch_number) BETWEEN 1 AND 50),
    expiry_date date,
    note text CHECK (char_length(note) <= 500),
    distributed_by uuid NOT NULL,
    latitude double precision CHECK (latitude BETWEEN -90 AND 90),
    longitude double precision CHECK (longitude BETWEEN -180 AND 180),
    accuracy_m double precision CHECK (accuracy_m >= 0),
    status text NOT NULL DEFAULT 'given' CHECK (status IN ('given', 'voided')),
    void_reason text CHECK (char_length(void_reason) <= 500),
    voided_by uuid,
    voided_at timestamptz,
    client_ref uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, number),
    UNIQUE (tenant_id, client_ref),
    CONSTRAINT samples_void_fields CHECK ((status = 'voided') = (voided_at IS NOT NULL AND void_reason IS NOT NULL AND voided_by IS NOT NULL)),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, distributed_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, voided_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX samples_customer_idx ON sample_distributions (tenant_id, customer_id, created_at DESC);
CREATE INDEX samples_created_idx ON sample_distributions (tenant_id, created_at DESC, id DESC);
CREATE INDEX samples_user_idx ON sample_distributions (tenant_id, distributed_by, created_at DESC);

ALTER TABLE sample_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE sample_counters FORCE ROW LEVEL SECURITY;
ALTER TABLE sample_distributions ENABLE ROW LEVEL SECURITY;
ALTER TABLE sample_distributions FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON sample_counters FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON sample_distributions FOR ALL
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
               ('ScientificRep', 'samples.record'), ('SalesRep', 'samples.record'), ('SalesManager', 'samples.read'), ('SalesManager', 'samples.manage'),
               ('CompanyManager', 'samples.read'), ('Supervisor', 'samples.read'),
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
FROM (VALUES ('ScientificRep', 'samples.record'), ('SalesRep', 'samples.record'), ('SalesManager', 'samples.read'), ('SalesManager', 'samples.manage'),
             ('CompanyManager', 'samples.read'), ('Supervisor', 'samples.read')) AS g(role_name, permission_key)
JOIN roles r ON r.name = g.role_name
ON CONFLICT DO NOTHING;
