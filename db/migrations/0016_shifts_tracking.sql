-- Shifts and basic tracking (plan step 1.13, PRD §6.2, decision D1: tracking only between an explicit shift start
-- and end). A user has at most one open shift. Points belong to a shift, come from the bound device and carry the
-- device's capture time, accuracy and mock flag. A retried upload is recognised by client_point_id, so the same
-- point is never stored twice. Coordinates are never written to audit or logs.
CREATE TABLE shifts (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    device_id uuid NOT NULL,
    started_at timestamptz NOT NULL DEFAULT now(),
    ended_at timestamptz,
    CHECK (ended_at IS NULL OR ended_at >= started_at),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, device_id) REFERENCES devices(tenant_id, id)
);
CREATE UNIQUE INDEX shifts_one_open_per_user ON shifts (tenant_id, user_id) WHERE ended_at IS NULL;
CREATE INDEX shifts_user_started_idx ON shifts (tenant_id, user_id, started_at DESC);

CREATE TABLE tracking_points (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    shift_id uuid NOT NULL,
    user_id uuid NOT NULL,
    device_id uuid NOT NULL,
    client_point_id uuid NOT NULL,
    geom geography(Point, 4326) NOT NULL,
    accuracy_m numeric(7,2) NOT NULL CHECK (accuracy_m > 0),
    captured_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    is_mocked boolean NOT NULL DEFAULT false,
    UNIQUE (shift_id, client_point_id),
    FOREIGN KEY (tenant_id, shift_id) REFERENCES shifts(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, device_id) REFERENCES devices(tenant_id, id)
);
CREATE INDEX tracking_points_shift_time_idx ON tracking_points (tenant_id, shift_id, captured_at);
CREATE INDEX tracking_points_user_time_idx ON tracking_points (tenant_id, user_id, captured_at DESC);

ALTER TABLE shifts ENABLE ROW LEVEL SECURITY;
ALTER TABLE shifts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON shifts
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE tracking_points ENABLE ROW LEVEL SECURITY;
ALTER TABLE tracking_points FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON tracking_points
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
