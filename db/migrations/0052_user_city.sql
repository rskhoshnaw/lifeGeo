-- The city of a staff member (plan step 4.37), shown in the users list and set from the panel (PUT /users/{id}/city). Optional.
ALTER TABLE users ADD COLUMN city text CHECK (char_length(btrim(city)) BETWEEN 1 AND 80);
