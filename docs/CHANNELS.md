# Message channels (step 3.4, decision D17)

Optional, free, secure and **off by default**. First channel: **Telegram**, for **staff only**. The messages of the in-app list (`notifications`: today "customer details changed" and "customer location changed") also arrive in the private Telegram chat of the user who linked it. Nothing goes to customers. SMS and WhatsApp are not built (they need a SIM or a paid account); they can use the same layer later.

## What the owner turns on (once, free)
1. In Telegram talk to **@BotFather** → `/newbot` → a name and a user name ending in `bot` → keep the **token** secret (never commit it).
2. Give it to the API (environment variables, one server per company):
   - `Channels__Enabled=true`
   - `Channels__Telegram__BotToken=<token>`
   - `Channels__Telegram__BotUsername=<user name without @>` (used to build the "open Telegram" link in the panel)
   - optional: `Channels__Language=ckb` (`ckb`, `ar`, `fa` or `en`; the language of the messages, one for the whole company because a user's own language is not stored on the server), `Channels__IntervalSeconds=15` (0 = job off).
3. Restart the API. Without `Enabled=true` **and** a token nothing is read, sent or consumed, the panel says "not turned on", and the three routes answer 503 (link) or `enabled: false` (status).

## How a user links (and why a message can never reach a stranger)
- In **Notifications** the user presses **Connect Telegram**. The server makes a one-time code `<company code>_<12 random characters>`, valid 15 minutes, only its SHA-256 hash is stored, one live code per user.
- The user opens the link `https://t.me/<bot>?start=<code>` (or sends `/start <code>` to the bot). The API reads what the bot received (`getUpdates`, every 15 s) and links that chat to the user who asked for the code.
- Only a **private** chat counts (a group is ignored) and only `/start <code>`; the code works once; the bot answers "connected" or "not valid or expired" (texts `msg.channel.*` in `i18n/strings.json`).
- The address is **never typed in** by the user or by an administrator, so nobody can point someone's messages at another person's chat. Linking and unlinking are written to the Audit (`channel.telegram_linked`, `channel.telegram_unlinked`).
- A chat belongs to one user: linking it again for another user moves it. If Telegram says the chat is gone or the bot was blocked, the link is dropped.

## Sending
- Same rules as push (step 2.14): a message is sent once (`notifications.channel_sent_at`), retried after temporary failures and given up after 5 tries, never sent when older than 24 h.
- A link brings only the messages created **after** it: messages of users without a link, or older than 24 h, are marked done without sending.
- Text: `msg.channel.customerProfileChanged` / `msg.channel.customerLocationChanged` with the customer name and code.

## Privacy
The message contains the **customer name and code** and passes through Telegram's servers. That is why it is opt-in per user, off per company until the owner decides, and the panel says so next to the button. Do not add phone numbers, prices or balances to channel messages.

## Routes (all need only a signed-in user; each user sees and changes only their own)
`GET /channels` (status), `POST /channels/telegram/link` (one-time code), `POST /channels/telegram/unlink`. The background job reports to `/health/ready` as `channels` (`docs/OBSERVABILITY.md`).

## Not built
SMS gateway, WhatsApp Cloud API, messages to customers, per-user language, manager alerts (security/operational) through channels. Each needs an owner decision (who receives what) and, for WhatsApp/SMS, an account or a SIM.
