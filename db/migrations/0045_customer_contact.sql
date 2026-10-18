-- More about a customer (plan step 4.15): the person to talk to, a second phone (or WhatsApp) and a free note. All optional and kept apart from
-- phone / address / place (PUT /customers/{id}/profile) so a client that does not know them never clears them.
ALTER TABLE customers ADD COLUMN contact_name text CHECK (char_length(contact_name) BETWEEN 1 AND 100);
ALTER TABLE customers ADD COLUMN phone2 text CHECK (char_length(phone2) BETWEEN 1 AND 40);
ALTER TABLE customers ADD COLUMN notes text CHECK (char_length(notes) BETWEEN 1 AND 500);
