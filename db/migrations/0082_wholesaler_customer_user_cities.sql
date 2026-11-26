-- Plan step 5.59 (owner decisions 2026-11-27):
--  * warehouses.customer_id: a warehouse of kind "wholesaler" is the same wholesaler that is in the customer list (a customer of type wholesaler); one customer has at most one warehouse.
--  * user_cities: the cities a sales person works in. At an order the person sees the company warehouses and the wholesalers of these cities (a warehouse without a city is seen by all;
--    a person with no city sees all, as before). The server checks it again when the order is placed.
ALTER TABLE warehouses ADD COLUMN customer_id uuid;
ALTER TABLE warehouses ADD CONSTRAINT warehouses_customer_fk FOREIGN KEY (tenant_id, customer_id) REFERENCES customers(tenant_id, id);
ALTER TABLE warehouses ADD CONSTRAINT warehouses_customer_kind_check CHECK (customer_id IS NULL OR kind = 'wholesaler');
CREATE UNIQUE INDEX warehouses_customer_uq ON warehouses (tenant_id, customer_id) WHERE customer_id IS NOT NULL;

CREATE TABLE user_cities (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    user_id uuid NOT NULL,
    city_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, user_id, city_id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, city_id) REFERENCES places(tenant_id, id)
);
ALTER TABLE user_cities ENABLE ROW LEVEL SECURITY;
ALTER TABLE user_cities FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON user_cities FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
