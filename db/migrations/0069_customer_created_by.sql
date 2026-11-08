-- Who added a customer (plan step 5.28, owner request 2026-11-08): the customer list shows "added by" and the date, and can be filtered by it.
-- The owner can change later (a manager hands customers to another user), so the creator is kept apart from owner_user_id.
-- Old customers: the user who wrote the "customer.created*" audit row, else the owner. (Doctor imports and the first setup wrote no such row; the owner is the best known person.)
ALTER TABLE customers ADD COLUMN created_by uuid;
ALTER TABLE customers ADD CONSTRAINT customers_created_by_fk FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id);
UPDATE customers c SET created_by = COALESCE(
    (SELECT a.actor_id FROM audit_logs a WHERE a.tenant_id = c.tenant_id AND a.entity_id = c.id::text AND a.action IN ('customer.created', 'customer.created_pending')
     ORDER BY a.occurred_at LIMIT 1),
    c.owner_user_id);
CREATE INDEX customers_owner_idx ON customers (tenant_id, owner_user_id);
CREATE INDEX customers_created_by_idx ON customers (tenant_id, created_by);
