-- Tenant management and explicit, audited cross-tenant access for the Super Admin (plan step 3.1, PRD: "Super Admin cross-tenant access is
-- explicit and audited").
--   * A fixed platform company (code 'platform') holds the Super Admin users; its role SuperAdmin has platform.tenants.manage (list, create,
--     rename, activate/deactivate companies, read the platform audit) and platform.tenants.access (read another company's audit log, with a
--     written reason). The API also requires that the caller belongs to the platform company, so a role named like this in an ordinary company
--     gives nothing. No Super Admin user is seeded: the owner creates the first one (docs/PLATFORM.md).
--   * platform_audit_logs: append-only record of every platform action (who, which company, why). A cross-company read writes its row in the
--     same transaction as the read, so there is no unaudited read.
INSERT INTO tenants (id, name, code, is_active) VALUES ('00000000-0000-0000-0000-00000000f001', 'Platform', 'platform', true) ON CONFLICT DO NOTHING;
INSERT INTO roles (id, tenant_id, name, data_scope, requires_device_binding)
SELECT gen_random_uuid(), '00000000-0000-0000-0000-00000000f001', 'SuperAdmin', 'tenant', false
WHERE NOT EXISTS (SELECT 1 FROM roles WHERE tenant_id = '00000000-0000-0000-0000-00000000f001' AND name = 'SuperAdmin');
INSERT INTO role_permissions (tenant_id, role_id, permission_key)
SELECT r.tenant_id, r.id, k.key FROM roles r, (VALUES ('platform.tenants.manage'), ('platform.tenants.access')) AS k(key)
WHERE r.tenant_id = '00000000-0000-0000-0000-00000000f001' AND r.name = 'SuperAdmin'
ON CONFLICT DO NOTHING;

CREATE TABLE platform_audit_logs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    actor_tenant_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    action text NOT NULL,
    target_tenant_id uuid REFERENCES tenants(id),
    reason text,
    detail text,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (actor_tenant_id, actor_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX platform_audit_logs_time_idx ON platform_audit_logs (occurred_at DESC);
CREATE INDEX platform_audit_logs_target_idx ON platform_audit_logs (target_tenant_id, occurred_at DESC);
CREATE TRIGGER platform_audit_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON platform_audit_logs
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();
