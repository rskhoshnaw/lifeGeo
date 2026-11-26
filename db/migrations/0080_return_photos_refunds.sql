-- Plan step 5.55 (owner decisions 2026-11-27: do the rest of the returns list):
--  * return_photos: up to three pictures of the goods on a return (damaged or expired goods), added by the author while the return is requested; read by whoever may read the return.
--  * payments.direction: 'in' is money the customer paid (all the payments so far), 'out' is money paid BACK to a customer (a refund). A refund is written by the finance manager (payments.manage) as
--    a confirmed payment without an order; it may not be more than the credit the customer has (approved returns + confirmed payments in - refunds - delivered), and it can be voided but not corrected.
--    The customer's balance counts it: Balance = Delivered - Returned - (confirmed in - confirmed out).
CREATE TABLE return_photos (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    return_id uuid NOT NULL,
    content_type text NOT NULL CHECK (content_type IN ('image/jpeg', 'image/png', 'image/webp')),
    data bytea NOT NULL CHECK (octet_length(data) BETWEEN 1 AND 3145728),
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, return_id) REFERENCES returns(tenant_id, id)
);
CREATE INDEX return_photos_return_idx ON return_photos (tenant_id, return_id);
ALTER TABLE return_photos ENABLE ROW LEVEL SECURITY;
ALTER TABLE return_photos FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON return_photos FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE payments ADD COLUMN direction text NOT NULL DEFAULT 'in' CHECK (direction IN ('in', 'out'));
ALTER TABLE payments ADD CONSTRAINT payments_refund_rules CHECK (direction = 'in' OR (order_id IS NULL AND status IN ('confirmed', 'voided')));
