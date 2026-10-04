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
| 1.17 panel: users, devices, customers | in PR (read routes `/me`, `/users`, `/roles`, `/devices` + panel screens) |
| Next | **1.18** panel: visit planning/assignment and region/team management |

Migrations: 0001–0018. Owner must apply 0006–0018 + `db/grants/lifedrop_app_grants.sql` on `lifedrop_dev`.
Open decisions: D3, D4, D6, D8, D9; API host (needed for panel deploy).

## Log (one line per step, newest last)
- 1.15: migration 0018, `/customer-approvals`, `POST /customers/{id}/approval`; DB 33/33.
- 1.16: `src/LifeDrop.Web`, CORS `Cors:AllowedOrigins`, CI job `web`; DB 34/34, panel 14 tests.
- 1.17: `UserEndpoints.cs` (`/me`, `/users`, `/roles`, `/devices`), panel tabs Users/Devices/Customers; no migration; DB 35/35, panel 19 tests.
