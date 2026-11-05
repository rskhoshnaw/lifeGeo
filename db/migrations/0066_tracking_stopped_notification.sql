-- Plan step 5.19: a new kind of notification, 'tracking.stopped', for a user whose open shift has had no contact (no position, no heartbeat) for 5 minutes.
-- entity_id is the shift id; the push text is fixed per language (Localizable.strings on iPhone, app.push.trackingStopped on Android).
ALTER TABLE notifications DROP CONSTRAINT notifications_kind_check;
ALTER TABLE notifications ADD CONSTRAINT notifications_kind_check
    CHECK (kind IN ('customer.profile_changed', 'customer.location_changed', 'manager.message', 'tracking.stopped'));
