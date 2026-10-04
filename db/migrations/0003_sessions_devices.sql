-- Apply once after 0002_login.sql. Existing web roles remain unbound by default.
ALTER TABLE roles ADD COLUMN requires_device_binding boolean NOT NULL DEFAULT false;

CREATE TABLE devices (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    device_label text NOT NULL,
    platform text NOT NULL CHECK (platform IN ('android', 'ios')),
    install_id text NOT NULL,
    app_version text NOT NULL,
    secret_hash bytea NOT NULL,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'blocked')),
    registered_at timestamptz NOT NULL DEFAULT now(),
    approved_at timestamptz,
    approved_by uuid,
    blocked_at timestamptz,
    blocked_by uuid,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, user_id, install_id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, approved_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, blocked_by) REFERENCES users(tenant_id, id)
);

CREATE TABLE refresh_tokens (
    id uuid PRIMARY KEY,
    family_id uuid NOT NULL,
    tenant_id uuid NOT NULL,
    user_id uuid NOT NULL,
    device_id uuid,
    token_hash bytea NOT NULL UNIQUE,
    created_at timestamptz NOT NULL DEFAULT now(),
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    replaced_by_id uuid,
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, device_id) REFERENCES devices(tenant_id, id),
    FOREIGN KEY (replaced_by_id) REFERENCES refresh_tokens(id)
);
CREATE INDEX refresh_tokens_family_active ON refresh_tokens(family_id)
    WHERE revoked_at IS NULL;
CREATE INDEX refresh_tokens_device_active ON refresh_tokens(device_id)
    WHERE revoked_at IS NULL AND device_id IS NOT NULL;
