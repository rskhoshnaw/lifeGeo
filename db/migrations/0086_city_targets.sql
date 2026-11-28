-- Sales targets per city (plan step 5.76, owner request 2026-11-28): for a city, a product and a month a target in the product's unit (boxes), next to what was really delivered to the
-- customers of that city. As for the targets of visitors (0058) the real sales are not typed in: they are the quantities delivered (order_items.delivered_quantity of orders in the status
-- "delivered", counted in the Iraq month of the delivery), here of the orders of every customer whose city (the place of kind city at or above his place) is this one, whoever placed them.
-- Same permissions as the visitor targets (targets.manage sets, targets.read reads), but the target of a city belongs to the whole company: only a reader or manager whose scope is the whole company
-- (sales manager, company manager, finance manager) may use them; a supervisor (region scope) may not.
CREATE TABLE city_targets (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    city_id uuid NOT NULL,
    product_id uuid NOT NULL,
    month date NOT NULL CHECK (month = date_trunc('month', month)::date),
    quantity numeric(18, 3) NOT NULL CHECK (quantity > 0 AND quantity <= 1000000),
    set_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, city_id, product_id, month),
    FOREIGN KEY (tenant_id, city_id) REFERENCES places(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id),
    FOREIGN KEY (tenant_id, set_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX city_targets_month_idx ON city_targets (tenant_id, month);

ALTER TABLE city_targets ENABLE ROW LEVEL SECURITY;
ALTER TABLE city_targets FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON city_targets
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
