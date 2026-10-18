-- Archiving a customer (plan step 4.11): a customer is never deleted (visits, orders, payments and reports refer to it); an archived one is hidden
-- from the lists, the map and from new visits, can be restored, and keeps all its history. Who archived it and when is in audit_logs
-- (customer.archived, customer.restored).
ALTER TABLE customers ADD COLUMN archived_at timestamptz;
