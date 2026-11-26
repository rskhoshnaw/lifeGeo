-- Report of the stock migration (plan step 5.53, migration 0078). On the server run deploy/stock-report.sh (it feeds this file to psql inside the database container).
-- 1. Old figures that could not be moved to a warehouse (no active company warehouse): count them in a warehouse by hand.
SELECT t.code AS company, p.code AS product, b.stock_qty, b.open_orders_qty
FROM legacy_stock_backup b JOIN products p ON p.tenant_id = b.tenant_id AND p.id = b.product_id JOIN tenants t ON t.id = b.tenant_id
WHERE b.moved_to_warehouse_id IS NULL ORDER BY t.code, p.code;
-- 2. Old figures that were moved: where to and how much (check them against the real shelves).
SELECT t.code AS company, p.code AS product, w.code AS warehouse, b.stock_qty AS old_figure, b.open_orders_qty, b.moved_quantity
FROM legacy_stock_backup b JOIN products p ON p.tenant_id = b.tenant_id AND p.id = b.product_id JOIN tenants t ON t.id = b.tenant_id
JOIN warehouses w ON w.tenant_id = b.tenant_id AND w.id = b.moved_to_warehouse_id ORDER BY t.code, p.code;
-- 3. Products nobody counted in any warehouse (they have no limit until a warehouse counts them).
SELECT t.code AS company, count(*) AS products_not_counted
FROM products p JOIN tenants t ON t.id = p.tenant_id
WHERE p.status = 'active' AND NOT EXISTS (SELECT 1 FROM warehouse_stock s WHERE s.tenant_id = p.tenant_id AND s.product_id = p.id)
GROUP BY t.code ORDER BY t.code;
