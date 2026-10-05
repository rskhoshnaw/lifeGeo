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
| 1.20 audit viewer in the panel | ✅ merged (PR #27) |
| 1.21 multilingual (ckb default, ar, fa RTL + en) | in PR; translations are Claude drafts, owner reviews `i18n/strings.json` |
| 1.22 end-to-end test | ✅ real phone + local DB: full visit to Completed, audit once per event, deliberate far check-in rejected (`visit.check_in_rejected`), app hides the repeat; direct server repeat is covered by DB tests only |
| 1.23 flexible planning part 1 (order, move to another day, customer visit hours) | in PR; plan section 1-ه lists 1.23-ب … 1.27 |
| Next | **1.23-ب** customer profile + street/neighbourhood list, then 1.24–1.27; **2.1** catalog and price (phase 1 exit also waits for the owner review of translations and the real-device tracking/offline tests) |

Migrations: 0001–0020. Owner must apply 0006–0020 + `db/grants/lifedrop_app_grants.sql` on `lifedrop_dev`.
Open decisions: D4, D6, D8, D9; API host (needed for panel deploy).

## Log (one line per step, newest last)
- 1.15: migration 0018, `/customer-approvals`, `POST /customers/{id}/approval`; DB 33/33.
- 1.16: `src/LifeDrop.Web`, CORS `Cors:AllowedOrigins`, CI job `web`; DB 34/34, panel 14 tests.
- 1.17: `UserEndpoints.cs` (`/me`, `/users`, `/roles`, `/devices`), panel tabs Users/Devices/Customers; no migration; DB 35/35, panel 19 tests.
- 1.18: `GET /visit-plan` (visits.write scope), `/users` also for visits.write; panel tabs Visits and Regions & teams; no migration; DB 36/36, panel 23 tests.
- 1.19: migration 0019 (`tracking.read`/`tracking.summary`, seed + backfill), `GET /live-map`, panel Live map (MapLibre 6 + OSM, lazy chunk); DB 37/37, app 48, panel 27 tests.
- 1.20: panel Audit tab (filters, Iraq-day bounds, cursor paging, read-only), `actorEmail` on `/audit-logs`; no migration; DB 37/37, panel 31 tests.
- 1.21: `i18n/strings.json` (292 keys × ckb/ar/fa/en) used by panel (`src/i18n.ts`, `components/I18n.tsx`) and app (`Mobile.Visits/Localization/Texts.cs`, `Mobile/AppLanguage.cs`); default ckb, RTL; tests check every key in every language + same placeholders + every app key used exists; panel 34, Mobile.Visits 139.
- 1.22 (done, owner on a real phone): panel on port 5273; Android keystore save error on first device registration is now logged (`adb logcat -s LifeDrop`); full visit completed on the phone (`scripts/local/Set-CustomerHere.ps1` moves a customer to the test spot).
- 1.23: migration 0020, `PlanOperations.cs` (`PlanVisits`), `PUT /visit-plan/order`, `POST /visits/{id}/postpone`, `/customers/{id}/visit-windows` (`CustomerVisitWindows`), `windowStatus` in plans; panel Visits/Customers, app plan buttons (online only); DB 39/39, Domain 172, Application 52, Mobile.Visits 162, panel 37 tests.
- Local setup (not a plan step): Docker DB, seed, PowerShell scripts, `docs/LOCAL-SETUP.md`; checked here: migrations + seed on the compose DB, API as lifedrop_app, register → approve → rep login, DB tests 37/37 against it.
