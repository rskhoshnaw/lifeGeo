-- The profile of a company, set by the platform's Super Admin and read by the apps and the panel before sign-in (plan step 4.48), so a new business type never needs a new app version:
--   industry        : 'pharma' | 'food' | null (null = the server's own default, Company:Industry); selects the built-in wording profile;
--   hidden_modules  : parts of the product this company does not use; the apps and the panel leave their buttons and tabs out;
--   text_overrides  : {"<text key>": {"<language>": "<text>"}}, wording of this company that replaces the built-in text of that key.
-- None of it is secret (GET /company-config/{code} is public); changing it needs platform.tenants.manage and is audited.
ALTER TABLE tenants ADD COLUMN industry text CHECK (industry IN ('pharma', 'food'));
ALTER TABLE tenants ADD COLUMN hidden_modules text[] NOT NULL DEFAULT '{}';
ALTER TABLE tenants ADD COLUMN text_overrides jsonb NOT NULL DEFAULT '{}';
