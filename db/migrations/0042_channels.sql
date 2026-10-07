-- Optional message channels (plan step 3.4, owner decision D17): free, secure, OFF by default. First channel: Telegram, for staff only.
--   * user_channels: where a user wants the messages of the in-app list (`notifications`) to arrive too. The address is the Telegram chat id of a PRIVATE
--     chat. It is set only by the user's own chat with the company bot, after proving it with a one-time code (never typed in by an administrator),
--     so a message can never go to a stranger.
--   * channel_link_codes: that one-time code (only its hash is kept), valid 15 minutes, one live code per user and channel.
--   * notifications.channel_sent_at / channel_attempts: the same idea as pushed_at / push_attempts of step 2.14, for the channel layer.
CREATE TABLE user_channels (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    channel text NOT NULL CHECK (channel IN ('telegram')),
    address text NOT NULL CHECK (length(address) BETWEEN 1 AND 100),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, user_id, channel),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    UNIQUE (tenant_id, channel, address)
);

CREATE TABLE channel_link_codes (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    channel text NOT NULL CHECK (channel IN ('telegram')),
    code_hash bytea NOT NULL,
    expires_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, user_id, channel),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX channel_link_codes_hash_idx ON channel_link_codes (tenant_id, code_hash);

ALTER TABLE user_channels ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_channels FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON user_channels
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE channel_link_codes ENABLE ROW LEVEL SECURITY;
ALTER TABLE channel_link_codes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON channel_link_codes
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE notifications ADD COLUMN channel_sent_at timestamptz, ADD COLUMN channel_attempts int NOT NULL DEFAULT 0;
-- Messages that exist before this step are never sent to a channel.
UPDATE notifications SET channel_sent_at = now();
CREATE INDEX notifications_unchannelled_idx ON notifications (tenant_id, created_at) WHERE channel_sent_at IS NULL;
