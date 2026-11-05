-- Plan step 5.20: the alert scan closes a shift that has had no contact for 30 minutes (ended_at = last contact) and marks it here. When the phone is heard from again
-- within 2 hours (a tunnel, a flat battery that was charged) the same shift is opened again, so the points queued on the phone during the gap are kept.
ALTER TABLE shifts ADD COLUMN auto_closed_at timestamptz;
