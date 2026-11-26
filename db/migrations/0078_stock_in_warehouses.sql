-- Stock lives only in the warehouses (plan step 5.53, owner decision 2026-11-26). Until now a product had two figures: the old single number products.stock_qty (taken when an order was placed,
-- step 4.41) and the stock of each warehouse (steps 5.35-5.42). From now on the old number is not read or written by the program; the total of a product is the sum of its warehouses and
-- what an order will take is "reserved" in its warehouse. A product nobody counted in any warehouse has no limit (as a product without a figure always had).
--
-- Nothing is lost: every old figure is copied into legacy_stock_backup first. A figure is also moved into the warehouse stock when the product is not counted in any warehouse yet: into the
-- first active company warehouse by code, as stock_qty + what the open orders (placed, preparing, ready) took from it (the hand-over takes those goods off the warehouse again, so they must be
-- there). A tenant without a company warehouse keeps the figure only in the backup (moved_to_warehouse_id stays empty) and the report scripts/db/stock-migration-report.sql lists it.
-- Running the migration again changes nothing. The column products.stock_qty stays (history, rollback); order_items.legacy_stock_taken now defaults to false: new lines never take from it.
CREATE TABLE IF NOT EXISTS legacy_stock_backup (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    product_id uuid NOT NULL,
    stock_qty numeric(18, 3) NOT NULL,
    open_orders_qty numeric(18, 3) NOT NULL DEFAULT 0,
    moved_to_warehouse_id uuid,
    moved_quantity numeric(18, 3),
    backed_up_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, product_id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, moved_to_warehouse_id) REFERENCES warehouses(tenant_id, id)
);
ALTER TABLE legacy_stock_backup ENABLE ROW LEVEL SECURITY;
ALTER TABLE legacy_stock_backup FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS tenant_isolation_policy ON legacy_stock_backup;
CREATE POLICY tenant_isolation_policy ON legacy_stock_backup FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

INSERT INTO legacy_stock_backup (tenant_id, product_id, stock_qty, open_orders_qty)
SELECT p.tenant_id, p.id, p.stock_qty,
       coalesce((SELECT sum(oi.quantity) FROM order_items oi JOIN orders o ON o.tenant_id = oi.tenant_id AND o.id = oi.order_id
                 WHERE oi.tenant_id = p.tenant_id AND oi.product_id = p.id AND oi.legacy_stock_taken AND o.status IN ('placed', 'preparing', 'ready')), 0)
FROM products p
WHERE p.stock_qty IS NOT NULL
ON CONFLICT (tenant_id, product_id) DO NOTHING;

WITH target AS (
    SELECT DISTINCT ON (w.tenant_id) w.tenant_id, w.id AS warehouse_id
    FROM warehouses w WHERE w.status = 'active' AND w.kind = 'company'
    ORDER BY w.tenant_id, w.code COLLATE "C"
), moved AS (
    INSERT INTO warehouse_stock (tenant_id, warehouse_id, product_id, quantity)
    SELECT b.tenant_id, t.warehouse_id, b.product_id, b.stock_qty + b.open_orders_qty
    FROM legacy_stock_backup b
    JOIN target t ON t.tenant_id = b.tenant_id
    WHERE b.moved_to_warehouse_id IS NULL
      AND NOT EXISTS (SELECT 1 FROM warehouse_stock s WHERE s.tenant_id = b.tenant_id AND s.product_id = b.product_id)
    ON CONFLICT DO NOTHING
    RETURNING tenant_id, product_id, warehouse_id, quantity
)
UPDATE legacy_stock_backup b SET moved_to_warehouse_id = m.warehouse_id, moved_quantity = m.quantity
FROM moved m WHERE b.tenant_id = m.tenant_id AND b.product_id = m.product_id;

ALTER TABLE order_items ALTER COLUMN legacy_stock_taken SET DEFAULT false;
