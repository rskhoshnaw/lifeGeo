-- One mobile / WhatsApp number per customer (owner request 2026-11-13): the second phone is merged into customers.phone and the
-- field is no longer used (the column stays so older app versions keep working; the server never fills it again).
-- A customer with no phone gets the second one; a customer with two different numbers keeps the first and the second goes to the note,
-- so nothing is lost. (The note is limited to 500 characters; a longer text is cut.)
UPDATE customers
SET notes = left(CASE WHEN notes IS NULL THEN 'Second number: ' || phone2 ELSE notes || E'\n' || 'Second number: ' || phone2 END, 500)
WHERE phone2 IS NOT NULL AND phone IS NOT NULL AND btrim(phone) <> btrim(phone2);

UPDATE customers SET phone = phone2 WHERE phone2 IS NOT NULL AND (phone IS NULL OR btrim(phone) = '');

UPDATE customers SET phone2 = NULL WHERE phone2 IS NOT NULL;
