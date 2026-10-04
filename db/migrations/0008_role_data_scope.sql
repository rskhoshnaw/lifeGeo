-- Data scope per role (plan step 0.6b, PRD §3): which rows a role's permissions reach.
--   tenant: the whole tenant (managers); region: the region of the user's team (supervisors);
--   own: only records the user owns or is assigned (field representatives).
-- Default 'tenant' keeps the behaviour of existing roles unchanged.
ALTER TABLE roles ADD COLUMN data_scope text NOT NULL DEFAULT 'tenant'
    CHECK (data_scope IN ('tenant', 'region', 'own'));
