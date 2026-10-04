-- Offline check-in / check-out (plan step 1.14, decision D5, 2026-10-04): saved on the phone without internet and
-- sent within 24 hours. Accepted offline attempts that the automatic checks could not confirm wait for a manager's
-- review; the decision is stored on the event and audited.
ALTER TABLE visit_location_events
    ADD COLUMN offline boolean NOT NULL DEFAULT false,
    ADD COLUMN elapsed_ms bigint CHECK (elapsed_ms >= 0),
    ADD COLUMN review_state text CHECK (review_state IN ('pending', 'approved', 'rejected')),
    ADD COLUMN review_reasons text[] NOT NULL DEFAULT '{}',
    ADD COLUMN reviewed_by uuid,
    ADD COLUMN reviewed_at timestamptz,
    ADD COLUMN review_note text CHECK (length(review_note) <= 500),
    ADD CONSTRAINT visit_location_events_reviewer_fk FOREIGN KEY (tenant_id, reviewed_by) REFERENCES users(tenant_id, id),
    -- Only an accepted offline attempt can need a review, and a decision always names its reviewer and time.
    ADD CONSTRAINT visit_location_events_review_check CHECK (
        (review_state IS NULL OR (offline AND accepted)) AND
        ((review_state IN ('approved', 'rejected')) = (reviewed_by IS NOT NULL AND reviewed_at IS NOT NULL)));

ALTER TABLE visit_location_events DROP CONSTRAINT visit_location_events_rejection_reason_check;
ALTER TABLE visit_location_events ADD CONSTRAINT visit_location_events_rejection_reason_check CHECK (rejection_reason IN
    ('outside_geofence', 'low_accuracy', 'stale_fix', 'clock_skew', 'mock_location', 'offline_expired'));

CREATE INDEX visit_location_events_review_idx ON visit_location_events (tenant_id, review_state, received_at)
    WHERE review_state IS NOT NULL;
