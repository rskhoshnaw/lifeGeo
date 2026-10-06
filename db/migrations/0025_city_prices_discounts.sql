-- Prices by city and discounts by customer (plan step 2.1, owner decision 2026-10-07; replaces the region/customer price rules of
-- migration 0024, which were never released).
--   products.base_price    : the general price. It applies everywhere unless a city has its own price for a period.
--   city_prices            : the price of one product in one CITY (a place of kind 'city' in the places tree) from valid_from to
--                            valid_to (inclusive; no end = open). A customer's city is read from its place. Rules of one product and
--                            city must not overlap (the API checks it); a rule is ended, never deleted.
--   customer_discounts     : a discount for one customer, on one product or (product_id NULL) on all products, as a percentage or as an
--                            amount per unit in the product's currency (amount needs a product). Valid between two days. Several can
--                            apply together; the API applies them oldest first, each on the price left, never below 0.
-- A customer without a place (or a place that is not under a city) pays the general price.
DROP TABLE price_rules;

CREATE TABLE city_prices (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    product_id uuid NOT NULL,
    city_id uuid NOT NULL,
    price numeric(18, 3) NOT NULL CHECK (price >= 0),
    valid_from date NOT NULL,
    valid_to date,
    created_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT city_prices_dates CHECK (valid_to IS NULL OR valid_to >= valid_from),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, city_id) REFERENCES places(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX city_prices_product_idx ON city_prices (tenant_id, product_id, city_id);

CREATE TABLE customer_discounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    customer_id uuid NOT NULL,
    product_id uuid,
    kind text NOT NULL CHECK (kind IN ('percent', 'amount')),
    value numeric(18, 3) NOT NULL CHECK (value > 0),
    valid_from date NOT NULL,
    valid_to date,
    created_by uuid,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT customer_discounts_percent_max CHECK (kind <> 'percent' OR value <= 100),
    CONSTRAINT customer_discounts_amount_needs_product CHECK (kind <> 'amount' OR product_id IS NOT NULL),
    CONSTRAINT customer_discounts_dates CHECK (valid_to IS NULL OR valid_to >= valid_from),
    FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX customer_discounts_customer_idx ON customer_discounts (tenant_id, customer_id);

ALTER TABLE city_prices ENABLE ROW LEVEL SECURITY;
ALTER TABLE city_prices FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON city_prices
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE customer_discounts ENABLE ROW LEVEL SECURITY;
ALTER TABLE customer_discounts FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON customer_discounts
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
