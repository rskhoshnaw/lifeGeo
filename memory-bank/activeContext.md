# Active Context

Updated: 2026-10-04.

## Current task
The remaining work is planned in `docs/IMPLEMENTATION-PLAN.md` (phases 0–3 with numbered steps). The plan and the decisions below are on branch `claude/jolly-bardeen-5q93e2`, PR #3. The next recommended step is **0.4**: run all `LifeDrop.Database.Tests` in CI on a disposable PostGIS database from an empty schema.

## Product decisions recorded 2026-10-04
- **D2:** Check-out requires fresh, valid GPS inside the customer's geofence radius, with the same anti-fraud checks as check-in. Recording any location is not enough.
- **D10:** The web panel is React + TypeScript (not Blazor), in a new `src/LifeDrop.Web` project.
- **D11:** Each team operates in one region; the sales manager defines regions and assigns them to teams. A representative may register an out-of-region customer; it is pending until an authorized manager approves it, then it belongs to that representative.
- Step 0.6 proposal (regions, teams, permission matrix) and the D11 approval queue were accepted.
- The inherited JSX/Express template was removed. `docs/PRD.md` was rewritten as the English working PRD with the decision log; `docs/06-CODING-CONVENTIONS.md` now matches current code and the React + TypeScript panel.
- Still open: D1, D3, D4, D5, D6, D8, D9 (see the plan, section 2).

## Relevant state
- GitHub remote `rskhoshnaw/lifeGeo` is connected. PR #1 and PR #2 are merged; `build` and `test-device-block` CI checks are green on `main` (`4cbbd1f`).
- Backend auth, device, role, customer and visit endpoints exist. `/start` and `/complete` change status only; server GPS/geofence and report checks are not implemented (steps 1.7, 1.8).
- `VisitApi_Failed_EnforcesPermissionsAndTransitions` is still absent (step 1.1).
- The Android MAUI app has Login/Home only; visit screens are not built.

## Next actions
1. Step 0.4 — all database tests in CI.
2. Step 1.1 — missing `fail` integration test.
3. Step 0.5 — common error contract.

Update this file after each meaningful change, and move durable facts to progress.md, systemPatterns.md, or techContext.md.
