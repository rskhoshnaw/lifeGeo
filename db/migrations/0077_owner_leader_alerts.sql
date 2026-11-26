-- Round 1 of the owner list of 2026-11-26 (plan step 5.52):
--  * notification kind customer.owner_changed: the user who is given customers is told (entity_id = how many),
--  * notification kind team.shift_stopped: the leader of a team is told when a member's open shift has no contact (entity_id = the shift; at most every 30 minutes per shift),
--  * alert kind connection_errors: five or more connection-error reports of one user's e-mail within 30 minutes (client_error_reports) go to the managers who read alerts.
ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check
    CHECK (kind IN ('customer.profile_changed', 'customer.location_changed', 'manager.message', 'tracking.stopped', 'feedback.new', 'feedback.reply', 'customer.owner_changed', 'team.shift_stopped'));

ALTER TABLE alerts DROP CONSTRAINT alerts_kind_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_kind_check CHECK (kind IN ('followup_overdue', 'visit_delayed', 'visit_long_stay', 'tracking_gap', 'new_device', 'gps_mocked',
    'impossible_speed', 'clock_tamper', 'outside_area', 'poor_accuracy', 'device_mismatch', 'impossible_travel', 'short_stay', 'low_coverage', 'stationary', 'short_shift', 'connection_errors'));
