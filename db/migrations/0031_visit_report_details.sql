-- Specialised visit forms (plan step 2.7, owner decisions 2026-10-07).
--   The base visit report (outcome + note) stays as it is. Beside it one "details" row per visit, written by the assigned user while InVisit:
--   common fields (purpose, person met and role, products presented, needs) and, by the visit type, the fields of that type only:
--     doctor: specialty, daily patients; pharmacy: approximate monthly sales (+ currency) and competitors; wholesaler: monthly purchase volume
--     (+ currency) and payment terms. The server refuses fields of another type. Photo and signature come later with file storage.
--   products_presented is a list of catalogue product ids (checked by the application, not a foreign key).
CREATE TABLE visit_report_details (
    visit_id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    purpose text NOT NULL CHECK (purpose IN ('product_intro', 'order_follow_up', 'collection', 'complaint', 'other')),
    person_met text CHECK (char_length(person_met) BETWEEN 1 AND 100),
    person_role text CHECK (char_length(person_role) BETWEEN 1 AND 100),
    needs text CHECK (char_length(needs) BETWEEN 1 AND 1000),
    product_ids uuid[] NOT NULL DEFAULT '{}' CHECK (cardinality(product_ids) <= 50),
    specialty text CHECK (char_length(specialty) BETWEEN 1 AND 100),
    daily_patients integer CHECK (daily_patients BETWEEN 0 AND 10000),
    monthly_sales numeric(18, 3) CHECK (monthly_sales >= 0),
    competitors text CHECK (char_length(competitors) BETWEEN 1 AND 500),
    monthly_volume numeric(18, 3) CHECK (monthly_volume >= 0),
    payment_terms text CHECK (char_length(payment_terms) BETWEEN 1 AND 500),
    currency text CHECK (currency IN ('IQD', 'USD')),
    submitted_by uuid NOT NULL,
    submitted_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT visit_report_details_currency CHECK ((monthly_sales IS NULL AND monthly_volume IS NULL) OR currency IS NOT NULL),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, submitted_by) REFERENCES users(tenant_id, id)
);

ALTER TABLE visit_report_details ENABLE ROW LEVEL SECURITY;
ALTER TABLE visit_report_details FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON visit_report_details FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
