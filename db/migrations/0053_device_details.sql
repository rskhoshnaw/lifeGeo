-- Devices (plan step 4.39): a label chosen by the manager (shown on the live map instead of the phone's own name), the phone model and the system version
-- (sent by newer app builds, optional), and a soft delete: a deleted device is hidden from the lists, blocked for good, and its install id is freed so the phone
-- can be registered again; its history (shifts, points, visits) stays.
ALTER TABLE devices ADD COLUMN admin_label text CHECK (char_length(btrim(admin_label)) BETWEEN 1 AND 80);
ALTER TABLE devices ADD COLUMN model text CHECK (char_length(model) BETWEEN 1 AND 100);
ALTER TABLE devices ADD COLUMN os_version text CHECK (char_length(os_version) BETWEEN 1 AND 40);
ALTER TABLE devices ADD COLUMN deleted_at timestamptz;
