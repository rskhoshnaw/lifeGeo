-- Check-in / check-out location evidence (plan steps 1.7, 1.8; PRD §6). Every attempt is kept, accepted or
-- rejected with a reason, so suspicious positions are never silently dropped (PRD §6.3).
CREATE TABLE visit_location_events (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    visit_id uuid NOT NULL,
    user_id uuid NOT NULL,
    device_id uuid,
    kind text NOT NULL CHECK (kind IN ('check_in', 'check_out')),
    accepted boolean NOT NULL,
    rejection_reason text CHECK (rejection_reason IN
        ('outside_geofence', 'low_accuracy', 'stale_fix', 'clock_skew', 'mock_location')),
    geom geography(Point, 4326) NOT NULL,
    accuracy_m numeric(7,2) NOT NULL,
    distance_m numeric(9,2),
    radius_m integer,
    captured_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now(),
    is_mocked boolean NOT NULL DEFAULT false,
    idempotency_key text CHECK (length(idempotency_key) BETWEEN 1 AND 100),
    CHECK (accepted = (rejection_reason IS NULL)),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, device_id) REFERENCES devices(tenant_id, id)
);

-- At most one accepted event per visit and kind; a retry with the same key is recognised as a replay.
CREATE UNIQUE INDEX visit_location_events_accepted_once ON visit_location_events (visit_id, kind) WHERE accepted;
CREATE INDEX visit_location_events_tenant_visit_idx ON visit_location_events (tenant_id, visit_id);

ALTER TABLE visit_location_events ENABLE ROW LEVEL SECURITY;
ALTER TABLE visit_location_events FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON visit_location_events
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
