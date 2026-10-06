-- Route history, playback and map layers (plan step 2.11, owner decisions 2026-10-07; closes decision D6).
--   Retention: the exact tracking points are kept for 90 days. Points of a shift that ended more than 90 days ago are deleted by the background job;
--   before that a row of shift_summaries is written (points, first and last time, longest gap, distance, fake points). Alerts, review results and the
--   summaries stay. History is read with tracking.read, only for users inside the reader's area; every read is written to the audit log.
--   Stops (computed when the route is read, not stored): at least 5 minutes within 50 metres.
CREATE TABLE shift_summaries (
    shift_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    point_count integer NOT NULL CHECK (point_count >= 0),
    first_point_at timestamptz,
    last_point_at timestamptz,
    max_gap_seconds integer NOT NULL CHECK (max_gap_seconds >= 0),
    distance_m numeric(12, 1) NOT NULL CHECK (distance_m >= 0),
    mocked_points integer NOT NULL CHECK (mocked_points >= 0),
    purged_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, shift_id) REFERENCES shifts(tenant_id, id)
);
ALTER TABLE shift_summaries ENABLE ROW LEVEL SECURITY;
ALTER TABLE shift_summaries FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON shift_summaries FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
