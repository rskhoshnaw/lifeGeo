-- Anti-fraud (plan step 2.10, owner decisions 2026-10-07).
--   More security alert kinds, made by the alert scan (2.9) from the tracking points of shifts that started in the last 48 hours. Every signal has a computed
--   definition and a fixed threshold (documented in docs/ANTIFRAUD.md; a panel setting may come later). The action is only an alert with the evidence kept in it
--   (alerts.evidence); nothing is blocked or hidden automatically. A manager reviews it: seen -> handled with a note AND a result of the review.
--     impossible_speed : two following points of a shift (by the phone's capture time) more than 1 km apart that need more than 180 km/h
--     clock_tamper     : a point captured more than 5 minutes AFTER the server received it (the phone's clock is ahead), or a check-in/out refused for clock skew.
--                        (Points that arrive late are normal: the phone keeps them offline; only a clock in the future is a signal.)
--     outside_area     : a point more than 30 km from every located customer of the region of the user's team (the region has no drawn border yet,
--                        its customers stand for the work area)
--     poor_accuracy    : 5 or more points of a shift with an accuracy worse than 200 m
--     device_mismatch  : a point of a shift sent by another phone than the one that started the shift
--   Review result (handled_outcome): valid | suspicious | fraud. Needed when a security alert about behaviour (every kind above and gps_mocked) is handled.
ALTER TABLE alerts DROP CONSTRAINT alerts_kind_check;
ALTER TABLE alerts ADD CONSTRAINT alerts_kind_check CHECK (kind IN ('followup_overdue', 'visit_delayed', 'visit_long_stay', 'tracking_gap', 'new_device', 'gps_mocked',
    'impossible_speed', 'clock_tamper', 'outside_area', 'poor_accuracy', 'device_mismatch'));
ALTER TABLE alerts ADD COLUMN evidence jsonb;
ALTER TABLE alerts ADD COLUMN handled_outcome text CHECK (handled_outcome IN ('valid', 'suspicious', 'fraud'));
ALTER TABLE alerts ADD CONSTRAINT alerts_outcome_only_when_handled CHECK (handled_outcome IS NULL OR status = 'handled');
