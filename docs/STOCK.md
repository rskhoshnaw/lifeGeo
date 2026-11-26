# Stock (موجودی) — how it works since step 5.53

**One source: the warehouses.** The total of a product is the sum of its counted stock in the active company warehouses (`warehouse_stock`). The old single figure `products.stock_qty` is not read or written by the program any more (the column stays for history).

## What the program does
- **Count:** a warehouse worker (own warehouses) or a manager (all) enters the quantity of each product in the panel (Warehouse tab, "Stock": cards or table, search, "only not counted") or in the app (Stock page).
- **Order:** an order for a warehouse *reserves* what it asks for (`reserved` = quantity of the open orders: placed, preparing, ready). If it asks for more than is free (`quantity − reserved`) the order is placed anyway and the sender gets a warning. A product nobody counted in that warehouse is not limited.
- **Hand-over to the driver** takes the prepared quantity (else the ordered one) off the warehouse stock; a failed or short delivery gives the goods back once. Cancelling an order only releases the reservation (nothing had left the warehouse).
- **Totals:** `GET /products` gives `stockQty` = sum of the warehouses (null when no warehouse counted the product) and `reservedQty`. The `stockQty` sent to `PUT /products/{id}/details` is ignored.

## Before the warehouses start (migration 0078)
1. `deploy/update.sh` (or `backup.sh` then `setup-db.sh`) runs migration 0078. It copies every old figure into `legacy_stock_backup` and, for a product not counted in any warehouse yet, puts `stock_qty + what its open orders took` into the first active company warehouse by code. Running it again changes nothing.
2. Check what happened: `/opt/lifedrop/deploy/stock-report.sh` (it runs `scripts/db/stock-migration-report.sql` inside the database container; the server has no `psql`) — (1) old figures that could not move (a company with no company warehouse: count them by hand), (2) figures that moved and where, (3) products nobody counted.
3. Create the warehouses (one per city) and give each worker their warehouse (Warehouse tab, staff) **before** the first count. Count every product once; until a product is counted somewhere it has no limit.
4. With no old stock in the system the migration is empty (the report shows no rows): nothing to check.

## Rules to remember
- A warehouse that is made inactive no longer counts in totals; its numbers stay and come back when it is active again.
- Never type stock into the product form: the field is gone; the number comes from the warehouses.

## Damaged bucket (5.56)
`warehouse_damaged_stock` holds damaged and expired goods per warehouse; it is not sold and not counted in `quantity`.

## Transfers between warehouses (5.60)
Goods move from one company warehouse to another in two steps (send, confirm receipt); sellable and damaged goods keep their kind; a smaller quantity at arrival is written on the line; the sender can cancel before arrival. See the access matrix for the endpoints.
