# Marketing messages (plan step 5.7)

## How it works
1. The sales manager writes **templates** in the panel (tab "Marketing messages"); `{name}` is replaced by the customer's name.
2. A user with `marketing.use` (sales manager, supervisor, sales rep, scientific rep) makes a **campaign** in the panel or the app: a name, a template and, optionally, customer types. The server queues **one message per approved customer** inside the user's own data scope that has a usable phone (8–15 digits; the same phone only once). Customers without a usable phone are counted as "skipped, no phone". A sales rep never gets doctors in a queue (the rule of `customers.doctors.read`).
3. In the **queue** the user opens each WhatsApp chat with one tap (`wa.me` link with the ready text), sends the message there himself and presses **Sent** (or **Skip**). The app asks for a pause of 2 minutes before the next chat opens.
4. A user sees only his own campaigns; a tenant-wide role (sales manager) sees everybody's.

## Why nothing is sent automatically
Automatic sending from a personal WhatsApp number (unofficial tools, bots) is against WhatsApp's rules; the number can be closed without warning, and no source gives a "safe" speed. Here the user sends every message himself, which is normal use. Telegram: a bot cannot start a chat with a phone number; only customers who started the company bot can be written to (see `docs/CHANNELS.md`), so Telegram is not part of the queue.

## WhatsApp Business API (optional)
Built, off by default: the server sends an approved Meta template to customers who agreed. Setup, consent, limits and cost: `docs/WHATSAPP-API.md`.

## API
See `docs/phase0-api-access-matrix.md` (section "Marketing messages"). Audit: `marketing.template_saved`, `marketing.campaign_created`.
