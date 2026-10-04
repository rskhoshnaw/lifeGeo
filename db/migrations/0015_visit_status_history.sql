-- Visit status history (plan step 1.9, PRD §12): every creation and status change of a visit, with the actor,
-- the time and the status before and after. A trigger on visits writes it, so no code path can change a status
-- without a history row. The writer names the actor with set_config('app.current_actor', ..., true) in the same
-- transaction; a status UPDATE without it is refused. An INSERT without it (data import) is kept with no actor.
-- No coordinates are stored here.
CREATE TABLE visit_status_history (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id uuid NOT NULL REFERENCES tenants(id),
    visit_id uuid NOT NULL,
    actor_id uuid,
    from_status text,
    to_status text NOT NULL,
    reason text,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    CHECK (actor_id IS NOT NULL OR from_status IS NULL),
    FOREIGN KEY (tenant_id, visit_id) REFERENCES visits(tenant_id, id),
    FOREIGN KEY (tenant_id, actor_id) REFERENCES users(tenant_id, id)
);
CREATE INDEX visit_status_history_visit_idx ON visit_status_history (tenant_id, visit_id, occurred_at);

-- Append-only, like audit_logs.
CREATE TRIGGER visit_status_history_immutable BEFORE UPDATE OR DELETE OR TRUNCATE ON visit_status_history
FOR EACH STATEMENT EXECUTE FUNCTION reject_audit_mutation();

ALTER TABLE visit_status_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE visit_status_history FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation_policy ON visit_status_history
    FOR ALL
    USING (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.current_tenant', true), '')::uuid);

CREATE FUNCTION record_visit_status_change() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    actor uuid := nullif(current_setting('app.current_actor', true), '')::uuid;
BEGIN
    IF TG_OP = 'UPDATE' THEN
        IF NEW.status IS NOT DISTINCT FROM OLD.status THEN RETURN NEW; END IF;
        IF actor IS NULL THEN
            RAISE EXCEPTION 'visit status change requires app.current_actor';
        END IF;
    END IF;
    INSERT INTO visit_status_history (tenant_id, visit_id, actor_id, from_status, to_status, reason)
    VALUES (NEW.tenant_id, NEW.id, actor,
            CASE WHEN TG_OP = 'UPDATE' THEN OLD.status END, NEW.status,
            CASE WHEN NEW.status IN ('Cancelled', 'Failed') THEN NEW.exception_reason END);
    RETURN NEW;
END; $$;

CREATE TRIGGER visits_status_history AFTER INSERT OR UPDATE OF status ON visits
FOR EACH ROW EXECUTE FUNCTION record_visit_status_change();
