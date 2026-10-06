-- Push messages to the phone (plan step 2.14, free Firebase Cloud Messaging; SMS is paid and not built).
--   * push_tokens: the FCM token of one phone for one user. A token belongs to one user at a time: registering it again
--     (another user signed in on the same phone) moves it.
--   * notifications.pushed_at / push_attempts: the in-app message is the source; a background job sends one data-only push
--     per message (kind + entity id; the app writes the localized text) and marks it. No token = nothing to send = marked.
CREATE TABLE push_tokens (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    token text NOT NULL CHECK (length(token) BETWEEN 20 AND 4096),
    platform text NOT NULL CHECK (platform IN ('android', 'ios')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    UNIQUE (tenant_id, token)
);
CREATE INDEX push_tokens_user_idx ON push_tokens (tenant_id, user_id);

ALTER TABLE push_tokens ENABLE ROW LEVEL SECURITY;
ALTER TABLE push_tokens FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON push_tokens
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE notifications ADD COLUMN pushed_at timestamptz, ADD COLUMN push_attempts int NOT NULL DEFAULT 0;
-- Messages that exist before this step are never pushed.
UPDATE notifications SET pushed_at = now();
CREATE INDEX notifications_unpushed_idx ON notifications (tenant_id, created_at) WHERE pushed_at IS NULL;
