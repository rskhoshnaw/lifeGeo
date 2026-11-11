-- Feedback from the field (plan step 5.30, owner request 2026-11-11): every user can send a suggestion, a complaint or a note about the work or about a customer (optionally linked to
-- a customer, with up to 3 photos); managers read them in the panel, answer and set a status. Who sees what: the author sees their own and the managers' answers; a user with
-- feedback.manage (CompanyManager, SalesManager: the whole company; Supervisor: the own region) reads, answers and sets the status of the feedback in their data scope.
-- Photos are kept in the database (like product photos: the normal backup has them), up to 3 MB each, JPEG, PNG or WEBP, and are served only to the author and to the managers in scope.
-- Two new notification kinds: feedback.new (to the managers in scope) and feedback.reply (to the author). seed_default_roles copied from 0064 with feedback.manage added.
CREATE TABLE feedback (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('suggestion', 'complaint', 'note')),
    body text NOT NULL CHECK (char_length(btrim(body)) BETWEEN 1 AND 2000),
    customer_id uuid,
    -- The region of the author's team when it was written: a supervisor reads the feedback of the own region.
    region_id uuid,
    status text NOT NULL DEFAULT 'new' CHECK (status IN ('new', 'in_review', 'done')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, region_id) REFERENCES regions(tenant_id, id)
);
CREATE INDEX feedback_tenant_created_idx ON feedback (tenant_id, created_at DESC);
CREATE INDEX feedback_user_idx ON feedback (tenant_id, user_id, created_at DESC);
CREATE INDEX feedback_customer_idx ON feedback (tenant_id, customer_id);

CREATE TABLE feedback_replies (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    feedback_id uuid NOT NULL,
    user_id uuid NOT NULL,
    body text NOT NULL CHECK (char_length(btrim(body)) BETWEEN 1 AND 1000),
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, feedback_id) REFERENCES feedback(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX feedback_replies_feedback_idx ON feedback_replies (tenant_id, feedback_id, created_at);

CREATE TABLE feedback_photos (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    feedback_id uuid NOT NULL,
    content_type text NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    data bytea NOT NULL CHECK (octet_length(data) BETWEEN 1 AND 3145728),
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, feedback_id) REFERENCES feedback(tenant_id, id)
);
CREATE INDEX feedback_photos_feedback_idx ON feedback_photos (tenant_id, feedback_id);

ALTER TABLE feedback ENABLE ROW LEVEL SECURITY;
ALTER TABLE feedback FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON feedback FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE feedback_replies ENABLE ROW LEVEL SECURITY;
ALTER TABLE feedback_replies FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON feedback_replies FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE feedback_photos ENABLE ROW LEVEL SECURITY;
ALTER TABLE feedback_photos FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON feedback_photos FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check
    CHECK (kind IN ('customer.profile_changed', 'customer.location_changed', 'manager.message', 'tracking.stopped', 'feedback.new', 'feedback.reply'));

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
SELECT r.tenant_id, r.id, 'feedback.manage'
FROM roles r
WHERE r.name IN ('CompanyManager', 'SalesManager', 'Supervisor')
ON CONFLICT DO NOTHING;
