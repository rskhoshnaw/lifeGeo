-- WhatsApp Business API (Cloud API) for marketing campaigns (plan step 5.7 follow-up, docs/WHATSAPP-API.md). The free way (the user opens each chat) stays. A campaign can instead be sent
-- by the server through the official API with a template that Meta approved: only to customers who agreed (customers.whatsapp_opt_in), one message at a time by a background job, retried
-- after temporary failures and given up after 3 tries (status "failed" with the reason). The token lives in the API's environment (one server per company), never in the database.
ALTER TABLE customers ADD COLUMN whatsapp_opt_in boolean NOT NULL DEFAULT false;
ALTER TABLE customers ADD COLUMN whatsapp_opt_in_at timestamptz;
ALTER TABLE customers ADD COLUMN whatsapp_opt_in_by uuid;
ALTER TABLE customers ADD CONSTRAINT customers_whatsapp_opt_in_by_fk FOREIGN KEY (tenant_id, whatsapp_opt_in_by) REFERENCES users(tenant_id, id);

-- The approved template of Meta that a message template maps to: its name and language, the first body variable {{1}} receives the customer's name.
ALTER TABLE marketing_templates ADD COLUMN whatsapp_name text CHECK (whatsapp_name ~ '^[a-z0-9_]{1,100}$');
ALTER TABLE marketing_templates ADD COLUMN whatsapp_language text CHECK (whatsapp_language ~ '^[a-z]{2,3}(_[A-Z]{2})?$');
ALTER TABLE marketing_templates ADD CONSTRAINT marketing_templates_whatsapp_pair CHECK ((whatsapp_name IS NULL) = (whatsapp_language IS NULL));

ALTER TABLE marketing_campaigns ADD COLUMN delivery text NOT NULL DEFAULT 'manual' CHECK (delivery IN ('manual', 'api'));
ALTER TABLE marketing_campaigns ADD COLUMN wa_template text;
ALTER TABLE marketing_campaigns ADD COLUMN wa_language text;
ALTER TABLE marketing_campaigns ADD CONSTRAINT marketing_campaigns_api_template CHECK (delivery = 'manual' OR (wa_template IS NOT NULL AND wa_language IS NOT NULL));

ALTER TABLE marketing_messages DROP CONSTRAINT marketing_messages_status_check;
ALTER TABLE marketing_messages ADD CONSTRAINT marketing_messages_status_check CHECK (status IN ('queued', 'sent', 'skipped', 'failed'));
ALTER TABLE marketing_messages ADD COLUMN attempts integer NOT NULL DEFAULT 0;
ALTER TABLE marketing_messages ADD COLUMN next_attempt_at timestamptz NOT NULL DEFAULT now();
ALTER TABLE marketing_messages ADD COLUMN last_error text;
ALTER TABLE marketing_messages ADD COLUMN provider_message_id text;
CREATE INDEX marketing_messages_due_idx ON marketing_messages (tenant_id, next_attempt_at) WHERE status = 'queued';
