-- Apply only to an empty, isolated LifeDrop database. Never run against live data.
CREATE TABLE tenants (
    id uuid PRIMARY KEY, name text NOT NULL, code text NOT NULL UNIQUE,
    is_active boolean NOT NULL DEFAULT true
);
CREATE TABLE users (
    id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
    is_active boolean NOT NULL DEFAULT true,
    UNIQUE (tenant_id, id)
);
CREATE TABLE roles (
    id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
    name text NOT NULL, UNIQUE (tenant_id, id), UNIQUE (tenant_id, name)
);
CREATE TABLE user_roles (
    tenant_id uuid NOT NULL, user_id uuid NOT NULL, role_id uuid NOT NULL,
    PRIMARY KEY (tenant_id, user_id, role_id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, role_id) REFERENCES roles(tenant_id, id)
);
CREATE TABLE role_permissions (
    tenant_id uuid NOT NULL, role_id uuid NOT NULL, permission_key text NOT NULL,
    PRIMARY KEY (tenant_id, role_id, permission_key),
    FOREIGN KEY (tenant_id, role_id) REFERENCES roles(tenant_id, id)
);
CREATE TABLE audit_logs (
    id uuid PRIMARY KEY, tenant_id uuid NOT NULL REFERENCES tenants(id),
    actor_id uuid NOT NULL, action text NOT NULL, entity_id text NOT NULL,
    occurred_at timestamptz NOT NULL,
    FOREIGN KEY (tenant_id, actor_id) REFERENCES users(tenant_id, id)
);
CREATE FUNCTION reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION 'audit_logs are append-only'; END; $$;
CREATE TRIGGER audit_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON audit_logs
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();
