-- One warehouse per city, with its own stock (owner request 2026-11-13).
--   warehouses.city_id / address / phone : where the warehouse is (a place of kind city), optional so the existing warehouses keep working.
--   warehouse_stock                      : how many units of each product a warehouse holds. A warehouse worker sees and changes only the
--                                          stock of the warehouses he works in (warehouse_staff); the managers see all of them.
ALTER TABLE warehouses ADD COLUMN city_id uuid;
ALTER TABLE warehouses ADD COLUMN address text CHECK (char_length(btrim(address)) BETWEEN 1 AND 300);
ALTER TABLE warehouses ADD COLUMN phone text CHECK (char_length(btrim(phone)) BETWEEN 1 AND 40);
ALTER TABLE warehouses ADD CONSTRAINT warehouses_city_fk FOREIGN KEY (tenant_id, city_id) REFERENCES places(tenant_id, id);
CREATE INDEX warehouses_city_idx ON warehouses (tenant_id, city_id);

CREATE TABLE warehouse_stock (
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
CREATE INDEX warehouse_stock_product_idx ON warehouse_stock (tenant_id, product_id);

ALTER TABLE warehouse_stock ENABLE ROW LEVEL SECURITY;
ALTER TABLE warehouse_stock FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON warehouse_stock FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
