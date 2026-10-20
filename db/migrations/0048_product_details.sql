-- More about a product (plan step 4.28): the specialty it is meant for (one key of the fixed list, checked by the API), the manufacturer, the expiry
-- day, the quantity in stock (null = stock is not tracked for this product) and a free note. All optional and saved apart from the main fields
-- (PUT /products/{id}/details) so a client that does not know them never clears them.
ALTER TABLE products ADD COLUMN specialty text CHECK (char_length(specialty) BETWEEN 1 AND 40);
ALTER TABLE products ADD COLUMN manufacturer text CHECK (char_length(manufacturer) BETWEEN 1 AND 100);
ALTER TABLE products ADD COLUMN expiry_date date;
ALTER TABLE products ADD COLUMN stock_qty numeric(18, 3) CHECK (stock_qty >= 0);
ALTER TABLE products ADD COLUMN notes text CHECK (char_length(notes) BETWEEN 1 AND 500);
