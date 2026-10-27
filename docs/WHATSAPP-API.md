# WhatsApp Business API for marketing (plan step 5.7 follow-up)

Optional and **off by default**. The free way of `docs/MARKETING.md` (the user opens each chat and sends it himself) stays. With this step a campaign can instead be **sent by the server** through the official **WhatsApp Business Cloud API** (Meta). No unofficial tool, no personal number.

## What Meta needs (once)
1. A **Meta Business** account and a **WhatsApp Business Account (WABA)**.
2. A phone number added to the WABA and verified. This number is the sender; it can no longer be used in the normal WhatsApp app.
3. A **permanent access token** (a System User of the Business with the `whatsapp_business_messaging` permission). Keep it secret.
4. The **Phone number ID** of that number (not the phone number itself).
5. A **message template** for marketing, created in Meta's WhatsApp Manager and **approved** by Meta. It must have **one body variable** `{{1}}`; the server puts the customer's name there. Note its **name** (lower case, digits and `_`) and **language code** (for example `ckb`, `ar`, `fa`, `en` or `en_US`).

Cost and limits are Meta's: marketing conversations are charged per message (price by country), a new number starts with a low daily limit that grows with quality, and a number with many blocks or reports can be restricted. Send only to people who really agreed.

## What the owner turns on (environment variables of the API, one server per company)
- `WhatsApp__Enabled=true`
- `WhatsApp__PhoneNumberId=<Phone number ID>`
- `WhatsApp__AccessToken=<permanent token>`
- optional: `WhatsApp__GraphVersion=v21.0`, `WhatsApp__MessagesPerMinute=30` (pause between two messages; 1 to 600), `WhatsApp__IntervalSeconds=10` (0 = job off).

Restart the API. Without all three the panel does not offer "send by the API", the campaign route answers 503 for `delivery: "api"`, and the background job does nothing. The token is read from the environment only; it is never stored in the database, logged or returned.

## How it works
1. **Message template** (panel → Marketing messages → Message templates): next to the normal text give the **WhatsApp template name** and **language code** of the approved template. Only a template with these two can be sent by the API. The normal text is still used for the free way and for the list on screen.
2. **Consent**: customers are sent a message only when `customers.whatsapp_opt_in` is true. It is set in the panel (Customers → Profile → "The customer agreed", `PUT /customers/{id}/whatsapp-consent`, audited `customer.whatsapp_consent_given` / `customer.whatsapp_consent_withdrawn`). Withdrawing stops a message that has not been sent yet.
3. **Campaign** (panel → New campaign → "How to send" → API): the server queues one message per customer who agreed and has a usable phone (the same phone once). The answer says how many were left out for a missing phone or missing consent.
4. **Sending**: a background job takes a small batch per company, sends one template message per call (`POST https://graph.facebook.com/<version>/<phone number id>/messages`, template name, language, body parameter = customer name), with a pause between messages. A message is claimed first (hidden for 5 minutes), so two jobs or a restart do not send it twice at once; if the server stops between the call and the saving of the result, that one message may be sent again after 5 minutes.
5. **Result**: `sent` (Meta's message id is kept), or after a temporary failure (rate limit 429, 5xx, network) tried again after 1 minute and 10 minutes, and **after 3 tries** `failed`. A refusal from Meta (wrong number, template not approved, token wrong) is `failed` at once with Meta's code and message. The campaign list shows queued / sent / failed; open the queue of an API campaign to see each message and its reason.
6. The hand-worked queue (panel and app) never lists the messages of an API campaign, and "sent" / "skip" on them answers 409.

## Not built
Delivery and read receipts (Meta webhooks), replies from customers, media templates (header image, buttons) and templates with more than one variable.

## Routes
`GET /marketing/status` (is the API on), `GET` and `PUT /customers/{id}/whatsapp-consent`, `delivery: "api"` on `POST /marketing/campaigns`, `whatsAppName` / `whatsAppLanguage` on the templates. See `docs/phase0-api-access-matrix.md`. The job reports to `/health/ready` as `whatsapp`.
