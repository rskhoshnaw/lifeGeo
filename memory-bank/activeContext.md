# Active Context

Updated: 2026-11-28. The app is in use on a production server (owner deploys with `deploy/update.sh`); owner decisions D1–D18 (plan section 2) and the owner's dated lists in the plan rows 5.x are authoritative. The current state and the open items are at the top of `memory-bank/phaseStatus.md`.

## Where we are
Steps up to 5.71 are merged (plan `docs/IMPLEMENTATION-PLAN.md`, rows 5.49–5.71 are the newest). The system runs for real users (field staff, warehouse staff, drivers, managers): API + PostgreSQL on the owner's server, React panel, MAUI app on Android (Play testing) and iOS (TestFlight, internal and external testers). Real-device checks of the newest features (returns, transfers, wholesaler orders, registration) are pending with the owner. Open items: see "Open / in progress" in `phaseStatus.md`.

## Decisions (details in the plan, section 2)
D1 explicit shift start/end, test target ≥90 % points and gap ≤5 min. D2 check-out inside the geofence. D3 finance manager sees tracking summary only (`tracking.summary`); exact positions need `tracking.read`, grantable later without code. D5 offline check-in/out up to 24 h, manager review when the monotonic clock disagrees or no tracking point is near the customer. D10/D12/D13 panel stack and hosting. Language: Sorani Kurdish (Iraqi, no Persian loanwords) is the default; ar, fa, en too (owner 2026-10-04). D11 one region per team; out-of-region customers pending until a sales manager or that region's supervisor approves. Open: D4, D6, D8, D9.

## Owner actions pending
- After every merge that changes the server or the database: `deploy/update.sh` on the server (migrations, grants, rebuild). After app changes: new iOS and Android builds (a fix in the app reaches users only with an installed build).
- Real-device tests of the newest app pages; first real run of the TestFlight distribution step (5.65).

## How to work (agreed with the owner)
- Reply in **Persian**, short. Times in Iraq local time (`Asia/Baghdad`).
- Develop on the branch named in the session instructions (now `claude/quirky-bohr-3gxmj4`; `CLAUDE.md` may still show the old name); after a PR merges, reset it to `origin/main` and push with `git push --force-with-lease`.
- Claude opens the PR **directly after the push** (standing owner instruction 2026-11-27: do not wait for the full database run; report its result later); the owner merges after CI is green. To save credit, Claude does **not** subscribe to PR events; the owner says "CI قرمز شد" if CI fails. Check the last PR's CI once at the start of each step.
- Commits end with the Co-Authored-By / Claude-Session lines; no model names in commits or PRs.

## Verification routine (per step)
- Local DB tests: start PostgreSQL with `pg_ctlcluster 16 main start` if needed; set `TEST_DATABASE_URL=Host=127.0.0.1;Port=5432;Database=lifedrop_test_ci;Username=test_user;Password=test_pass;Include Error Detail=true`, `TEST_SUPERUSER_DATABASE_URL` (same with `Username=postgres;Password=temp_root`; without it the deployment rehearsal tests are skipped) and **`Auth__RefreshReplayGraceSeconds=0`** (as CI; without it `RoleAssignmentDatabaseTests` fails); Postgres stops now and then: `service postgresql start`; run `dotnet test tests/LifeDrop.Database.Tests` (twice) and `dotnet test LifeDrop.CI.slnf`.
- New migration: add it to `LifeDrop.Database.Tests.csproj`, the list in `CustomerApiIntegrationTests.SetupIsolatedSchemaAsync` (m1..mN) and `DeploymentRehearsalTests`; update the grants file; never edit merged migrations (change `seed_default_roles` with `CREATE OR REPLACE` in a new migration, copying the latest version, now 0085).
- API change: regenerate `docs/api/openapi-v1.json` with `UPDATE_OPENAPI_SNAPSHOT=1` and check the diff is intended. New DB test → raise `MIN_EXPECTED_TESTS` in `.github/workflows/database-tests.yml` to the **real total** of the last full run (now 128; registering a migration adds no test; a value above the total fails CI).
- Mutation check: break the new rule briefly, confirm a test fails, restore.
- Scripts under `deploy/` have a real-database test (`deploy/tests/backup-scripts.sh`, run by CI `database-tests`: psql/pg_dump/pg_restore inside the database container, as on the server); the load test is `scripts/perf/` (README with results). Keep the seed `scripts/perf/seed-load.sql` fitting new tables (CI loads it).
- Android build check (works here): `USER=root dotnet build src/LifeDrop.Mobile/LifeDrop.Mobile.csproj -f net10.0-android -p:AndroidSdkDirectory=$HOME/android-sdk`; iOS builds only on the macOS runner (`ios.yml`).
- New table → grants in `db/grants/lifedrop_app_grants.sql`; `GrantsCoverageTests` scans the C# SQL and fails when a grant is missing (tests run as the schema owner and would not notice).
- A migration test registration (3 places) needs the scratchpad helper only to save typing; do it by hand if the helper is gone.
- Mobile: logic goes in `src/LifeDrop.Mobile.Visits` (tested in CI). MAUI pages: compile check with `dotnet build src/LifeDrop.Mobile/LifeDrop.Mobile.csproj -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 -p:OutputType=Library`. Android-only files: compile in a scratch project referencing `~/.dotnet/packs/Microsoft.Android.Ref.36/*/ref/net10.0/Mono.Android.dll` (the Android SDK cannot be downloaded here). CI job `mobile-android` builds the real app.
- SQL ordering in tests: use `COLLATE "C"` (CI uses en_US).
- Update the plan row, `docs/phase0-api-access-matrix.md`, and one log line in `memory-bank/phaseStatus.md` in the same PR (old evidence is in `memory-bank/archive/`).
- Texts: never hard-code user-facing text; add the key to `i18n/strings.json` in all four languages (tests fail otherwise). Tests run in English: panel `test-setup.ts`, .NET module initializers in `TextsTests.cs` and `Database.Tests/EnglishTexts.cs`. Run the DB suite too when mobile client text changes.

## A bug that comes back (rules from steps 5.49 / 5.63 / 5.64)
The phone-registration bug was "fixed" in 5.49 and came back in 5.63: the fix mended the login path only, while the cause named in its own plan row (the register path trusting the phone's stored copy) stayed.
- When a report names a cause, `grep` for every place with the same pattern before closing it; fix the cause, not the one screen the user saw.
- Walk the user's whole way in a scenario test (all buttons, any order, repeated), not only the changed function. Example: `DeviceLifecycleScenarioTests`. Check that it fails on the old code.
- A change to the app does not reach users until a new build is installed. Write "needs a new build" in the plan row and the reply, and do not call the report solved before the owner has tested the new build on a real phone.
- The server logs are lost when the container is rebuilt (`update.sh`): ask for them right after a new attempt, not afterwards. The panel's Devices page shows each phone's app version and build.
