-- The mobile number of a staff member (plan step 5.22), set from the panel (PUT /users/{id}/phone) and used to open a WhatsApp chat from the live map and the users list. Optional.
ALTER TABLE users ADD COLUMN phone text CHECK (char_length(btrim(phone)) BETWEEN 1 AND 40);
