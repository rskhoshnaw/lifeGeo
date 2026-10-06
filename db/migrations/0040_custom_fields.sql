-- Custom visit-form fields (plan step 3.2, part 2: form builder). A manager of a company (permission forms.manage: SalesManager, CompanyManager)
-- defines extra fields for the visit report details (2.7): text, number, choice, yes/no, date; for all visit types or one type; required or not;
-- labels in any of the four languages (at least one). A field is never deleted: it is made inactive, so old answers stay readable. The key, type and
-- visit type of a field cannot change. The assigned user answers while the visit is InVisit; the whole answer set is replaced on each save, the
-- server checks every answer against the active field definitions (and that required fields are answered).
CREATE TABLE form_fields (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    key text NOT NULL CHECK (key ~ '^[a-z][a-z0-9_]{1,39}$'),
    field_type text NOT NULL CHECK (field_type IN ('text', 'number', 'choice', 'yes_no', 'date')),
    applies_to text NOT NULL CHECK (applies_to IN ('all', 'pharmacy', 'doctor', 'wholesaler', 'other')),
    label_en text CHECK (char_length(label_en) BETWEEN 1 AND 100),
    label_ckb text CHECK (char_length(label_ckb) BETWEEN 1 AND 100),
    label_ar text CHECK (char_length(label_ar) BETWEEN 1 AND 100),
    label_fa text CHECK (char_length(label_fa) BETWEEN 1 AND 100),
    options text[] NOT NULL DEFAULT '{}' CHECK (cardinality(options) <= 20),
    is_required boolean NOT NULL DEFAULT false,
    sort_order integer NOT NULL DEFAULT 0 CHECK (sort_order BETWEEN 0 AND 1000),
    is_active boolean NOT NULL DEFAULT true,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CHECK (label_en IS NOT NULL OR label_ckb IS NOT NULL OR label_ar IS NOT NULL OR label_fa IS NOT NULL),
    CHECK ((field_type = 'choice') = (cardinality(options) >= 2)),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, key)
);

CREATE TABLE visit_custom_answers (
    visit_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    answers jsonb NOT NULL,
    submitted_by uuid NOT NULL,
    submitted_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, submitted_by) REFERENCES users(tenant_id, id)
);

ALTER TABLE form_fields ENABLE ROW LEVEL SECURITY;
ALTER TABLE form_fields FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON form_fields FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE visit_custom_answers ENABLE ROW LEVEL SECURITY;
ALTER TABLE visit_custom_answers FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON visit_custom_answers FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

-- seed_default_roles (copied from 0036) with forms.manage added; existing companies get the permission too.
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
               ('SalesManager', 'forms.manage'), ('CompanyManager', 'forms.manage')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT tenant_id, id, 'forms.manage' FROM roles WHERE name IN ('SalesManager', 'CompanyManager')
ON CONFLICT DO NOTHING;
