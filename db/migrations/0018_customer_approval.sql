-- Customers registered by field users (plan step 1.15, PRD §8, decision D11). A customer created in the creator's
-- own region (or by a user with tenant-wide scope) is approved at once. A customer a rep registers outside their
-- region waits as 'pending' until a sales manager (tenant) or the supervisor of that region approves or rejects it;
-- it stays with the rep who registered it. Visits can be planned only for approved customers.
ALTER TABLE customers
    ADD COLUMN approval_status text NOT NULL DEFAULT 'approved' CHECK (approval_status IN ('approved', 'pending', 'rejected')),
    ADD COLUMN approved_by uuid,
    ADD COLUMN approved_at timestamptz,
    ADD COLUMN approval_note text CHECK (length(approval_note) <= 500),
    ADD CONSTRAINT customers_approver_fk FOREIGN KEY (tenant_id, approved_by) REFERENCES users(tenant_id, id);
CREATE INDEX customers_pending_idx ON customers (tenant_id, region_id) WHERE approval_status = 'pending';

-- customers.approve: decide on customers registered outside the registering rep's region.
CREATE OR REPLACE FUNCTION seed_default_roles(p_tenant uuid) RETURNS void LANGUAGE sql AS $$
    WITH defaults(name, data_scope, requires_device_binding) AS (
        VALUES ('CompanyManager', 'tenant', false),
               ('SalesManager', 'tenant', false),
               ('Supervisor', 'region', false),
               ('FinanceManager', 'tenant', false),
               ('SalesRep', 'own', true),
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
               ('SalesManager', 'device.approve'), ('SalesManager', 'user.manageroles'), ('SalesManager', 'sessions.kill'), ('SalesManager', 'audit.read'),
               ('Supervisor', 'customers.read'), ('Supervisor', 'customers.write'), ('Supervisor', 'customers.location.write'), ('Supervisor', 'customers.geofence.write'), ('Supervisor', 'visits.write'), ('Supervisor', 'visits.cancel'), ('Supervisor', 'customers.approve'),
               ('Supervisor', 'regions.read'), ('Supervisor', 'teams.read'),
               ('FinanceManager', 'customers.read'), ('FinanceManager', 'audit.read'),
               ('SalesRep', 'customers.read'), ('SalesRep', 'customers.write'), ('SalesRep', 'customers.location.write'), ('SalesRep', 'visits.fail'), ('SalesRep', 'visits.create_unplanned'),
               ('ScientificRep', 'customers.read'), ('ScientificRep', 'visits.fail'), ('ScientificRep', 'visits.create_unplanned')
    )
    INSERT INTO role_permissions (tenant_id, role_id, permission_key)
    SELECT p_tenant, r.id, g.permission_key
    FROM grants g
    JOIN roles r ON r.tenant_id = p_tenant AND r.name = g.role_name
    ON CONFLICT DO NOTHING;
$$;

INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, 'customers.approve'
FROM roles r WHERE r.name IN ('SalesManager', 'Supervisor')
ON CONFLICT DO NOTHING;
