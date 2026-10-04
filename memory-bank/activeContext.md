# Active Context

Updated: 2026-10-04 (handoff for a new session).

## Where we are
Work follows `docs/IMPLEMENTATION-PLAN.md` step by step. Phase 0 is done (exit report `docs/PHASE0-EXIT-REPORT.md`). Phase 1:
- Backend 1.1–1.9 merged (PRs #5–#13): planning fields, Iraq "today", reassign/cancel, customer location and radius, visit report, server check-in/check-out with GPS + geofence (D2), status history and audit of every transition.
- Mobile 1.10–1.14 merged (PRs #14–#21): daily plan, visit details, check-in/report/check-out, shifts and Android background tracking, offline outbox. All need a **real-device check** (owner): `docs/TRACKING-DEVICE-TEST.md`.
- Step **1.15** (customer registration by reps, D11 approval queue, migration 0018) is in its PR on branch `claude/jolly-bardeen-5q93e2`.
- Next: **1.16** web panel (React + TypeScript + Tailwind v4, D10/D12; Cloudflare Pages, D13). The API host must be decided before 1.16.

## Decisions (details in the plan, section 2)
D1 explicit shift start/end, test target ≥90 % points and gap ≤5 min. D2 check-out inside the geofence. D5 offline check-in/out up to 24 h, manager review when the monotonic clock disagrees or no tracking point is near the customer. D10/D12/D13 panel stack and hosting. D11 one region per team; out-of-region customers pending until a sales manager or that region's supervisor approves. Open: D3, D4, D6, D8, D9.

## Owner actions pending
- Apply migrations **0006–0018** and `db/grants/lifedrop_app_grants.sql` on `lifedrop_dev`.
- Real-device tests (mobile steps, tracking, offline).

## How to work (agreed with the owner)
- Reply in **Persian**, short. Times in Iraq local time (`Asia/Baghdad`).
- Develop on `claude/jolly-bardeen-5q93e2`; after a PR merges, reset that branch to `origin/main` and force-push.
- Claude opens the PR; the owner merges after CI is green. To save credit, Claude does **not** subscribe to PR events; the owner says "CI قرمز شد" if CI fails. Check the last PR's CI once at the start of each step.
- Commits end with the Co-Authored-By / Claude-Session lines; no model names in commits or PRs.

## Verification routine (per step)
- Local DB tests: start PostgreSQL with `pg_ctlcluster 16 main start` if needed; set `TEST_DATABASE_URL` (test_user/test_pass, db `lifedrop_test_ci`) and `TEST_SUPERUSER_DATABASE_URL` (postgres/temp_root); run `dotnet test tests/LifeDrop.Database.Tests` (twice) and `dotnet test LifeDrop.CI.slnf`.
- New migration: add it to `LifeDrop.Database.Tests.csproj`, the list in `CustomerApiIntegrationTests.SetupIsolatedSchemaAsync` (m1..mN) and `DeploymentRehearsalTests`; update the grants file; never edit merged migrations (change `seed_default_roles` with `CREATE OR REPLACE` in a new migration, copying the latest version, now 0018).
- API change: regenerate `docs/api/openapi-v1.json` with `UPDATE_OPENAPI_SNAPSHOT=1` and check the diff is intended. New DB test → raise `MIN_EXPECTED_TESTS` in `.github/workflows/database-tests.yml` (now 33).
- Mutation check: break the new rule briefly, confirm a test fails, restore.
- Mobile: logic goes in `src/LifeDrop.Mobile.Visits` (tested in CI). MAUI pages: compile check with `dotnet build src/LifeDrop.Mobile/LifeDrop.Mobile.csproj -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 -p:OutputType=Library`. Android-only files: compile in a scratch project referencing `~/.dotnet/packs/Microsoft.Android.Ref.36/*/ref/net10.0/Mono.Android.dll` (the Android SDK cannot be downloaded here). CI job `mobile-android` builds the real app.
- SQL ordering in tests: use `COLLATE "C"` (CI uses en_US).
- Update the plan row, `docs/phase0-api-access-matrix.md`, and `memory-bank/progress.md` in the same PR.
