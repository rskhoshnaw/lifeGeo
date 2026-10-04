# Phase status (read this first)

Updated: 2026-10-04. Plan and step table: `docs/IMPLEMENTATION-PLAN.md`. Full history and test evidence:
`memory-bank/archive/` (read only when needed).

| Phase / steps | State |
| --- | --- |
| Phase 0 (0.1–0.10) | ✅ code + CI (`docs/PHASE0-EXIT-REPORT.md`) |
| 1.1–1.9 backend visits | ✅ merged (PRs #5–#13) |
| 1.10–1.14 mobile (plan, visit, check-in/out, shifts + tracking, offline) | ✅ code + CI (PRs #14–#21); **real-device test pending** (`docs/TRACKING-DEVICE-TEST.md`) |
| 1.15 customer registration + approval (D11) | ✅ server (PR #22); mobile form and panel queue later |
| 1.16 web panel skeleton + sign-in + CORS | ✅ merged (PR #23); Cloudflare Pages setup by owner |
| 1.17 panel: users, devices, customers | ✅ merged (PR #24) |
| 1.18 panel: visit planning, regions/teams | ✅ merged (PR #25) |
| 1.19 basic live map (D3 decided: finance = summary) | ✅ merged (PR #26) |
| 1.20 audit viewer in the panel | in PR (panel tab Audit, `actorEmail` on audit rows) |
| Next | **1.21** multilingual (ku, ar, fa RTL + en) in mobile and panel |

Migrations: 0001–0019. Owner must apply 0006–0019 + `db/grants/lifedrop_app_grants.sql` on `lifedrop_dev`.
Open decisions: D4, D6, D8, D9; API host (needed for panel deploy).

## Log (one line per step, newest last)
- 1.15: migration 0018, `/customer-approvals`, `POST /customers/{id}/approval`; DB 33/33.
- 1.16: `src/LifeDrop.Web`, CORS `Cors:AllowedOrigins`, CI job `web`; DB 34/34, panel 14 tests.
- 1.17: `UserEndpoints.cs` (`/me`, `/users`, `/roles`, `/devices`), panel tabs Users/Devices/Customers; no migration; DB 35/35, panel 19 tests.
- 1.18: `GET /visit-plan` (visits.write scope), `/users` also for visits.write; panel tabs Visits and Regions & teams; no migration; DB 36/36, panel 23 tests.
- 1.19: migration 0019 (`tracking.read`/`tracking.summary`, seed + backfill), `GET /live-map`, panel Live map (MapLibre 6 + OSM, lazy chunk); DB 37/37, app 48, panel 27 tests.
- 1.20: panel Audit tab (filters, Iraq-day bounds, cursor paging, read-only), `actorEmail` on `/audit-logs`; no migration; DB 37/37, panel 31 tests.
