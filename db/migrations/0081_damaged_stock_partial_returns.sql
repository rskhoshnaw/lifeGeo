-- Plan step 5.56 (owner decisions 2026-11-27):
--  * warehouse_damaged_stock: the damaged and expired goods a warehouse holds apart from the stock that can be sold. An approved return line whose goods go "damaged" is added here, to the
--    warehouse chosen on the return; a manager or the warehouse worker can also write sellable stock off into it (expired or damaged) and set the figure after the goods were thrown away.
--    The sellable stock (warehouse_stock) is never touched by these goods; a product that nobody counted does not become "counted" because of damaged goods.
--  * warehouse_writeoffs: the history of those write-offs (who, how much, why).
--  * return_items.decision: a sales manager may approve some lines of a return and reject the others ('approved' / 'rejected'; NULL while undecided, and for returns decided before this).
--    Only the approved lines count (stock, damaged goods, the balance via returns.total); a rejected line frees its quantity on the order again.
CREATE TABLE warehouse_damaged_stock (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    warehouse_id uuid NOT NULL,
    product_id uuid NOT NULL,
    quantity numeric(18, 3) NOT NULL CHECK (quantity >= 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    updated_by uuid,
    PRIMARY KEY (tenant_id, warehouse_id, product_id),
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, updated_by) REFERENCES users(tenant_id, id)
);
CREATE TABLE warehouse_writeoffs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    warehouse_id uuid NOT NULL,
    product_id uuid NOT NULL,
    quantity numeric(18, 3) NOT NULL CHECK (quantity > 0),
    reason text NOT NULL CHECK (reason IN ('expired', 'damaged')),
    note text CHECK (char_length(note) <= 300),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX warehouse_writeoffs_idx ON warehouse_writeoffs (tenant_id, warehouse_id, created_at DESC);
ALTER TABLE warehouse_damaged_stock ENABLE ROW LEVEL SECURITY;
ALTER TABLE warehouse_damaged_stock FORCE ROW LEVEL SECURITY;
ALTER TABLE warehouse_writeoffs ENABLE ROW LEVEL SECURITY;
ALTER TABLE warehouse_writeoffs FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON warehouse_damaged_stock FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON warehouse_writeoffs FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

ALTER TABLE return_items ADD COLUMN decision text CHECK (decision IN ('approved', 'rejected'));
-- The lines of returns approved before this existed were all approved.
UPDATE return_items i SET decision = 'approved' FROM returns r WHERE r.tenant_id = i.tenant_id AND r.id = i.return_id AND r.status = 'approved' AND i.decision IS NULL;

-- Damaged goods of returns approved before this existed are put in the warehouse of their return (once: stock_applied marks them).
WITH moved AS (
    UPDATE return_items i SET stock_applied = true
    FROM returns r
    WHERE r.tenant_id = i.tenant_id AND r.id = i.return_id AND r.status = 'approved' AND r.warehouse_id IS NOT NULL AND i.destination = 'damaged' AND i.stock_applied IS NULL
    RETURNING i.tenant_id, r.warehouse_id, i.product_id, i.quantity
)
INSERT INTO warehouse_damaged_stock (tenant_id, warehouse_id, product_id, quantity)
SELECT tenant_id, warehouse_id, product_id, sum(quantity) FROM moved GROUP BY tenant_id, warehouse_id, product_id
ON CONFLICT (tenant_id, warehouse_id, product_id) DO UPDATE SET quantity = warehouse_damaged_stock.quantity + EXCLUDED.quantity, updated_at = now();
