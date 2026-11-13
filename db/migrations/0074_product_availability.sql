-- A product can be out of stock for a while and come back (owner request 2026-11-13): `available` = false keeps it in the lists (marked "currently unavailable")
-- but nobody can order it; deactivating (status) removes it completely. `min_stock` is the quantity at or below which a warehouse's stock of it is shown as low (optional).
ALTER TABLE products ADD COLUMN available boolean NOT NULL DEFAULT true;
ALTER TABLE products ADD COLUMN min_stock numeric(18, 3) CHECK (min_stock >= 0);
