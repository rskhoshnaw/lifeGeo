-- Saved and scheduled reports (plan step 3.2, part 1: report builder). A user saves the filters of the KPI report (docs/KPI.md) under a name,
-- runs it any time, and may schedule it (daily, weekly or monthly at an Iraq hour). A run stores the whole result, so a scheduled report can be read
-- later exactly as it was. A scheduled run executes as its owner with the owner's current permission and area; if the owner lost the permission the
-- run is stored as failed. No new permission: reports.read.
--   range_kind: relative ranges are worked out in Iraq days when the report runs; 'fixed' uses from_date and to_date.
CREATE TABLE saved_reports (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    owner_user_id uuid NOT NULL,
    name text NOT NULL CHECK (length(name) BETWEEN 1 AND 100),
    range_kind text NOT NULL CHECK (range_kind IN ('yesterday', 'last_7_days', 'last_30_days', 'this_month', 'last_month', 'fixed')),
    from_date date,
    to_date date,
    region_id uuid,
    team_id uuid,
    customer_type text CHECK (customer_type IS NULL OR length(customer_type) <= 50),
    frequency text CHECK (frequency IN ('daily', 'weekly', 'monthly')),
    weekday smallint CHECK (weekday BETWEEN 1 AND 7),
    day_of_month smallint CHECK (day_of_month BETWEEN 1 AND 28),
    run_hour smallint CHECK (run_hour BETWEEN 0 AND 23),
    is_active boolean NOT NULL DEFAULT true,
    next_run_at timestamptz,
    last_run_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (tenant_id, owner_user_id) REFERENCES users(tenant_id, id),
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, owner_user_id, name),
    CHECK ((range_kind = 'fixed') = (from_date IS NOT NULL AND to_date IS NOT NULL)),
    CHECK (range_kind = 'fixed' OR (from_date IS NULL AND to_date IS NULL)),
    CHECK (from_date IS NULL OR to_date >= from_date),
    CHECK (frequency IS NOT NULL OR (weekday IS NULL AND day_of_month IS NULL AND run_hour IS NULL AND next_run_at IS NULL)),
    CHECK (frequency IS NULL OR run_hour IS NOT NULL),
    CHECK ((frequency = 'weekly') = (weekday IS NOT NULL)),
    CHECK ((frequency = 'monthly') = (day_of_month IS NOT NULL))
);
CREATE INDEX saved_reports_due_idx ON saved_reports (tenant_id, next_run_at) WHERE is_active AND frequency IS NOT NULL;

CREATE TABLE report_runs (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    report_id uuid NOT NULL,
    trigger text NOT NULL CHECK (trigger IN ('manual', 'schedule')),
    status text NOT NULL CHECK (status IN ('ok', 'failed')),
    error_code text,
    from_date date NOT NULL,
    to_date date NOT NULL,
    ran_at timestamptz NOT NULL DEFAULT now(),
    result jsonb,
    FOREIGN KEY (tenant_id, report_id) REFERENCES saved_reports(tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX report_runs_report_idx ON report_runs (tenant_id, report_id, ran_at DESC);

ALTER TABLE saved_reports ENABLE ROW LEVEL SECURITY;
ALTER TABLE saved_reports FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON saved_reports FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
ALTER TABLE report_runs ENABLE ROW LEVEL SECURITY;
ALTER TABLE report_runs FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON report_runs FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);
