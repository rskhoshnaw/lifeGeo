-- The state of the phone during a shift (plan step 5.17, owner request 2026-11-05). Many field users went grey on the live map: their phone had stopped sending and nobody knew why
-- (app closed, no "Allow all the time", battery saving, notifications off, an old app version, a phone maker that kills background apps). While the app is alive it now reports once a
-- minute even when there is no new position (a phone that stands still), together with these facts. One row per shift, replaced at every report; seen_at is the server clock.
-- nudged_at is when the server last sent the "tracking stopped, open the app" message for this shift (step 5.18), so the same message is not sent again at once.
CREATE TABLE shift_health (
    shift_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    seen_at timestamptz NOT NULL,
    app_version text CHECK (length(app_version) <= 40),
    platform text CHECK (platform IN ('android', 'ios')),
    os_version text CHECK (length(os_version) <= 40),
    device_model text CHECK (length(device_model) <= 80),
    location_permission text NOT NULL DEFAULT 'unknown' CHECK (location_permission IN ('always', 'when_in_use', 'denied', 'unknown')),
    precise_location boolean,
    location_services boolean,
    notifications boolean,
    battery_exempt boolean,
    background_refresh text CHECK (background_refresh IN ('available', 'denied', 'restricted')),
    battery_percent smallint CHECK (battery_percent BETWEEN 0 AND 100),
    low_power boolean,
    nudged_at timestamptz,
    FOREIGN KEY (tenant_id, shift_id) REFERENCES shifts(tenant_id, id)
);
CREATE INDEX shift_health_seen_idx ON shift_health (tenant_id, seen_at DESC);

ALTER TABLE shift_health ENABLE ROW LEVEL SECURITY;
ALTER TABLE shift_health FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON shift_health
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
