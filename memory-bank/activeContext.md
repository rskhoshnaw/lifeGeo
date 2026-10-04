# Active Context

Updated: 2026-10-04.

## Current task
The remaining work is planned in `docs/IMPLEMENTATION-PLAN.md` (phases 0–3 with numbered steps). The plan and decisions D2, D10, D11 were merged to `main` in PR #3 (`bc70472`). Step **0.4** is done: PR #4 (template removal, rewritten PRD and conventions, all database tests in CI) merged at `49c7244`; CI `database-tests` ran 14/14 with 0 skipped. Next step: **1.1**.

## Product decisions recorded 2026-10-04
- **D2:** Check-out requires fresh, valid GPS inside the customer's geofence radius, with the same anti-fraud checks as check-in. Recording any location is not enough.
- **D10:** The web panel is React + TypeScript (not Blazor), in a new `src/LifeDrop.Web` project.
- **D12:** The panel uses Tailwind CSS v4 (Vite plugin) with logical RTL-safe utilities; it is installed when step 1.16 creates `src/LifeDrop.Web`.
- **D11:** Each team operates in one region; the sales manager defines regions and assigns them to teams. A representative may register an out-of-region customer; it is pending until an authorized manager approves it, then it belongs to that representative.
- Step 0.6 proposal (regions, teams, permission matrix) and the D11 approval queue were accepted.
- The inherited JSX/Express template was removed. `docs/PRD.md` was rewritten as the English working PRD with the decision log; `docs/06-CODING-CONVENTIONS.md` now matches current code and the React + TypeScript panel.
- Still open: D1, D3, D4, D5, D6, D8, D9 (see the plan, section 2).

## Relevant state
- GitHub remote `rskhoshnaw/lifeGeo` is connected. PR #1 and PR #2 are merged; `build` and `test-device-block` CI checks are green on `main` (`4cbbd1f`).
- Backend auth, device, role, customer and visit endpoints exist. `/start` and `/complete` enforce device, GPS and geofence on the server (steps 1.7, 1.8); `/complete` also requires the saved report.
- Step 1.1: `VisitApi_Failed_EnforcesPermissionsAndTransitions` added (no token 401, owner without `visits.fail` 403, other owner 403, other tenant 404, blank reason 400, success 204 with persisted reason, repeat 400 without overwrite). Local 15/15 database tests; awaiting CI. Visit transitions still write no audit rows (step 1.9).
- The Android MAUI app has Login/Home only; visit screens are not built.

## Next actions
1. Steps 1.1 and 0.5 are merged and verified in CI (PR #5).
2. Step 0.5b — OpenAPI contract file `docs/api/openapi-v1.json` with drift test; local 17/17; awaiting CI on its PR. Next: step 0.6.
3. Phase 0 complete in code/CI (PR #6, exit report). Owner action: apply migrations 0006–0015 + `db/grants/lifedrop_app_grants.sql` on `lifedrop_dev`. Steps 1.2, 1.3 merged (PR #7). Steps 1.4–1.6 merged (PRs #8–#10). Step 1.7 merged (PR #11). Step 1.8 merged (PR #12). Step 1.9 merged (PR #13). Phase 1 backend (1.1–1.9) done. Step 1.10 (mobile daily plan) in its PR; MAUI page needs an Android build and device check. Next: 1.11. D13: panel on Cloudflare Pages (private repo, PR previews, Access); API host to decide before 1.16.

Update this file after each meaningful change, and move durable facts to progress.md, systemPatterns.md, or techContext.md.

## Working preferences
- Report times to the user in Iraq local time (`Asia/Baghdad`, UTC+3).
- Claude opens PRs itself and drives them to green; the owner merges.
