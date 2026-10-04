-- Visit report (plan step 1.6, PRD §5.3). One report per visit, written by the assigned user while the
-- visit is InVisit; check-out (step 1.8) will require it. Outcome values are a versioned contract shared
-- with mobile (contract_version 1). There is no "order placed" outcome until real orders exist (phase 2).
CREATE TABLE visit_reports (
    visit_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    outcome text NOT NULL
        CHECK (outcome IN ('products_presented', 'order_intent', 'follow_up_needed', 'no_interest', 'other')),
    note text NOT NULL CHECK (length(btrim(note)) BETWEEN 3 AND 2000),
    contract_version smallint NOT NULL DEFAULT 1 CHECK (contract_version = 1),
    submitted_by uuid NOT NULL,
    submitted_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, submitted_by) REFERENCES users(tenant_id, id)
);

ALTER TABLE visit_reports ENABLE ROW LEVEL SECURITY;
ALTER TABLE visit_reports FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON visit_reports
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
