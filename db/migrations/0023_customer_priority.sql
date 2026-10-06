-- Priority of a customer set by a manager (plan step 1.26, owner decision 2026-10-06): the sales manager or the supervisor
-- of a region marks customers (or all customers of a place) as high / normal / low. It is shown to the field user in the
-- plan and the customer list and puts the high ones first in the suggestions when planning. It does not change the priority of
-- visits that were already made.
ALTER TABLE customers
    ADD COLUMN priority text NOT NULL DEFAULT 'normal' CHECK (priority IN ('high', 'normal', 'low')),
    ADD COLUMN priority_set_by uuid,
    ADD COLUMN priority_set_at timestamptz,
    ADD CONSTRAINT customers_priority_set_by_fk FOREIGN KEY (tenant_id, priority_set_by) REFERENCES users(tenant_id, id);
