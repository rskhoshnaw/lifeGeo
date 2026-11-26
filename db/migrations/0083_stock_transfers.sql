-- Plan step 5.60 (owner decisions 2026-11-27): goods move between warehouses (the central warehouse to the cities, the cities back to the central one with their returns and damaged goods,
-- and city to city). Two steps: the sender's warehouse worker (or a manager) sends, which takes the quantity out of the sender at once (status "sent", on the road); the receiving
-- warehouse's worker (or a manager) confirms what arrived, which adds it to the receiver (status "received"; a smaller quantity than sent is written down and the difference is shown).
-- A transfer that has not arrived yet may be cancelled by the sender, which gives the goods back. Each line is either sellable stock ('sellable') or damaged / expired goods ('damaged').
-- Only company warehouses take part (a wholesaler keeps no stock).
CREATE TABLE stock_transfer_counters (
    tenant_id uuid PRIMARY KEY REFERENCES tenants(id),
    last_number bigint NOT NULL
);
CREATE TABLE stock_transfers (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    number bigint NOT NULL,
    from_warehouse_id uuid NOT NULL,
    to_warehouse_id uuid NOT NULL,
    status text NOT NULL DEFAULT 'sent' CHECK (status IN ('sent', 'received', 'cancelled')),
    note text CHECK (char_length(note) <= 500),
    client_ref uuid NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    closed_by uuid,
    closed_at timestamptz,
    close_note text CHECK (char_length(close_note) <= 500),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, number),
    UNIQUE (tenant_id, client_ref),
    CONSTRAINT stock_transfers_two_warehouses CHECK (from_warehouse_id <> to_warehouse_id),
    CONSTRAINT stock_transfers_closed_fields CHECK ((status = 'sent') = (closed_at IS NULL)),
    FOREIGN KEY (tenant_id, from_warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, to_warehouse_id) REFERENCES warehouses(tenant_id, id),
    FOREIGN KEY (tenant_id, created_by) REFERENCES users(tenant_id, id),
    FOREIGN KEY (tenant_id, closed_by) REFERENCES users(tenant_id, id)
);
CREATE INDEX stock_transfers_from_idx ON stock_transfers (tenant_id, from_warehouse_id, created_at DESC);
CREATE INDEX stock_transfers_to_idx ON stock_transfers (tenant_id, to_warehouse_id, created_at DESC);
CREATE TABLE stock_transfer_items (
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    transfer_id uuid NOT NULL,
    line_no int NOT NULL,
    product_id uuid NOT NULL,
    product_code text NOT NULL,
    product_name text NOT NULL,
    unit text NOT NULL,
    bucket text NOT NULL CHECK (bucket IN ('sellable', 'damaged')),
    quantity numeric(18, 3) NOT NULL CHECK (quantity > 0),
    received_quantity numeric(18, 3) CHECK (received_quantity >= 0 AND received_quantity <= quantity),
    PRIMARY KEY (tenant_id, transfer_id, line_no),
    FOREIGN KEY (tenant_id, transfer_id) REFERENCES stock_transfers(tenant_id, id),
    FOREIGN KEY (tenant_id, product_id) REFERENCES products(tenant_id, id)
);
ALTER TABLE stock_transfer_counters ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_transfer_counters FORCE ROW LEVEL SECURITY;
ALTER TABLE stock_transfers ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_transfers FORCE ROW LEVEL SECURITY;
ALTER TABLE stock_transfer_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE stock_transfer_items FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON stock_transfer_counters FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON stock_transfers FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
CREATE POLICY tenant_isolation_policy ON stock_transfer_items FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
