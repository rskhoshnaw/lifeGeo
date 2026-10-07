# Owner decisions (recorded 2026-10-07)

Source: owner chat. Update this file when a decision changes.

## Working method
- Everything is done on GitHub (cloud session). Nothing is changed on the owner's local machine; the owner pulls later.
- **Nothing is uploaded to a production server until all tests and features of the panel and the apps are finished.**
- When deployment starts, the owner knows nothing about servers: guide step by step, exact commands, one step at a time.

## Server
- RMR-Soft offer (managed VPS, no root/SSH, 12–24 requests/year, IPv4 and backup unpriced) is **rejected**.
- Use a plain VPS with root access (see "Server options" below), deploy with `docs/DEPLOY.md`.
- Target: 4 vCPU / 8 GB RAM / 80 GB NVMe, Ubuntu 24.04, API + database on one machine, ≤50 field users, 1 point/min, 30 days point retention.
- Backup: separate, encrypted, outside the server (D9: up to 24 h data loss, 4 h restore).
- Domain: to be bought later.

## Server options (check current prices before buying)
1. Hetzner Cloud, 4 vCPU / 8 GB class (Germany or Finland) — best price/quality, root access, snapshots; backup: Hetzner Storage Box or Backblaze B2.
2. Contabo VPS (similar size, cheaper, slower disks/support).
3. Turkish provider only if a Turkey location is required (not needed technically).

## Accounts
- Apple Developer and Google Play developer accounts: the owner will buy when asked. Ask at the step where it is needed:
  - Google Play account: before the first Play/Internal-testing upload.
  - Apple Developer account: before native iOS (TestFlight) work.
- Pending: iPhone route (PWA without background GPS vs native iOS with Apple account) — owner has accepted buying the account, so native iOS via GitHub macOS runner is the plan.

## SMS / WhatsApp (plan 3.4)
- Build as an optional provider layer, **disabled by default**; activate only when the owner says.
- Prefer free/open options: Telegram bot, self-hosted Android SMS gateway (own SIM), WhatsApp Business Cloud API (free only inside the 24 h customer-service window; templates are paid).
- Anything that costs money or needs a secret stays off until the owner activates it; secrets only in `deploy/.env` on the server.

## Architecture clarification (2026-10-07, later)
- **One VPS (API + database) per company**, fully isolated; one mobile app (Android + iOS) for all. The app derives the company server from the company code (base domain at build time) and then reads that server's public company config.
- First customer: the company "Life Drops" (pharma). Developer credit: Pixelo Design (https://pixelo-design.com/); support +9647510686828.
- The product is also to be installed for a **food distributor**: wording profile `COMPANY_INDUSTRY=food` (shops/restaurants instead of pharmacies/doctors). More wording changes are added as `key@food` texts.
