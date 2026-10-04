-- Apply once after 0001_role_assignment.sql.
ALTER TABLE users ADD COLUMN email text;
ALTER TABLE users ADD COLUMN password_hash text;
CREATE UNIQUE INDEX users_tenant_email_unique ON users (tenant_id, lower(email))
    WHERE email IS NOT NULL;

CREATE TABLE auth_login_audit (
    id uuid PRIMARY KEY,
    tenant_code text NOT NULL,
    email_sha256 bytea NOT NULL,
    tenant_id uuid REFERENCES tenants(id),
    user_id uuid,
    succeeded boolean NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id)
);
CREATE TRIGGER auth_login_audit_immutable
BEFORE UPDATE OR DELETE OR TRUNCATE ON auth_login_audit
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();
