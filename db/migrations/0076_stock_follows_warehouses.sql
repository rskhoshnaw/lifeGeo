-- The total stock of a product is the sum of its warehouses (owner request 2026-11-13).
--   order_items.legacy_stock_taken : the order line took its quantity from the old single figure products.stock_qty (step 4.41); only those lines give it back on cancel.
--                                    Old lines keep true (that was the only mechanism then). A product counted in the warehouse of the order no longer touches the old figure.
--   orders.stock_left_warehouse    : the hand-over took the order's goods off the warehouse stock (step 5.38); a failed or short delivery gives them back once. Old orders stay false,
--                                    so nothing is added back for goods that were never taken off.
ALTER TABLE order_items ADD COLUMN legacy_stock_taken boolean NOT NULL DEFAULT true;
ALTER TABLE orders ADD COLUMN stock_left_warehouse boolean NOT NULL DEFAULT false;
