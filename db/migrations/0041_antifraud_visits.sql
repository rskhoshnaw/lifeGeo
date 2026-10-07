-- Anti-fraud without dense tracking (plan step 4.8). Three more alert kinds made by the alert scan from visit check-in/out events and shift summaries
-- (thresholds in AlertRules, documented in docs/ANTIFRAUD.md; nothing is blocked automatically):
--     impossible_travel : two accepted visit events of one user, from different visits, more than 1 km apart that need more than 180 km/h (security)
--     short_stay        : a visit whose accepted check-out came less than 2 minutes after the accepted check-in (security)
--     low_coverage      : a finished shift of at least 1 hour with fewer than 50 % of the expected tracking points (one per 3 minutes) (operational)
-- impossible_travel and short_stay need a review result (valid | suspicious | fraud) when handled; low_coverage does not.
ALTER TABLE alerts DROP CONSTRAINT alerts_kind_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_kind_check CHECK (kind IN ('followup_overdue', 'visit_delayed', 'visit_long_stay', 'tracking_gap', 'new_device', 'gps_mocked',
    'impossible_speed', 'clock_tamper', 'outside_area', 'poor_accuracy', 'device_mismatch', 'impossible_travel', 'short_stay', 'low_coverage'));
