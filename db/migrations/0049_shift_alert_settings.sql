-- Per-company settings of the shift alerts (plan step 4.33), changed by the sales manager in the panel:
--   * the first `shift_window_hours` hours of a shift are watched; a shift that ends before that gets a `short_shift` alert;
--   * a rep who stays in one place (within the fixed radius) for more than `stationary_minutes` in that window gets a `stationary` alert.
-- No row = the defaults (3 hours, 20 minutes).
CREATE TABLE shift_alert_settings (
    tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
    shift_window_hours integer NOT NULL DEFAULT 3 CHECK (shift_window_hours BETWEEN 1 AND 12),
    stationary_minutes integer NOT NULL DEFAULT 20 CHECK (stationary_minutes BETWEEN 5 AND 120),
    updated_by uuid,
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, updated_by) REFERENCES users(tenant_id, id)
);

ALTER TABLE shift_alert_settings ENABLE ROW LEVEL SECURITY;
ALTER TABLE shift_alert_settings FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON shift_alert_settings
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

-- Two new operational alert kinds: stationary (in one place too long in the first hours of a shift) and short_shift (the shift ended before those hours were over).
ALTER TABLE alerts DROP CONSTRAINT alerts_kind_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_kind_check CHECK (kind IN ('followup_overdue', 'visit_delayed', 'visit_long_stay', 'tracking_gap', 'new_device', 'gps_mocked',
    'impossible_speed', 'clock_tamper', 'outside_area', 'poor_accuracy', 'device_mismatch', 'impossible_travel', 'short_stay', 'low_coverage', 'stationary', 'short_shift'));
