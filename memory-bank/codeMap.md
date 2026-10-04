# Code map (use instead of scanning the repo)

## Backend (.NET 10, raw SQL with Npgsql, PostgreSQL 16 + PostGIS)
- `src/LifeDrop.Domain/Visits/` — `Visit` state machine, `VisitReport`, `LocationCheck.cs` (GpsFix, LocationCheckPolicy, OfflineCheckPolicy).
- `src/LifeDrop.Application/` — use cases by area: `Visits/VisitOperations.cs` (create, en-route, arrive, fail, manage, reports, `VisitCheckpoint` check-in/out), `Visits/OfflineReviews.cs`, `Customers/CustomerOperations.cs` (create + `CustomerApprovals`), `Tracking/ShiftTracking.cs`, `Roles/` (`IPermissionStore`, `PermissionKeys`, data scope tenant/region/own), `Auth/`, `Users/UserDirectory.cs` (panel reads: me, users, roles, devices).
- `src/LifeDrop.Infrastructure/` — Postgres stores (`Visits/PostgresVisitStore.cs`, `Customers/…`, `Tracking/PostgresShiftStore.cs`). Every write sets `app.current_tenant` (RLS) and writes its audit row in the same transaction; visit writes also set `app.current_actor` (status-history trigger).
- `src/LifeDrop.API/Program.cs` — DI, auth/JWT, CORS, older routes; `Endpoints/*.cs` — newer routes; `ApiErrors.cs` — ProblemDetails codes.
- `db/migrations/00NN_*.sql` (never edit merged ones), `db/grants/lifedrop_app_grants.sql`.
- Contract: `docs/api/openapi-v1.json`; route/permission matrix: `docs/phase0-api-access-matrix.md`.

## Tests
- `tests/LifeDrop.Database.Tests/CustomerApiIntegrationTests.cs` — API + DB tests (large file: grep for the test name; helpers `SetupIsolatedSchemaAsync`, `GenerateToken`, `AssertProblemAsync`, `FakeSessionStore`, `FixedClock`). `DeploymentRehearsalTests.cs` — restricted DB role.
- `tests/LifeDrop.{Domain,Application,Mobile.Auth,Mobile.Visits}.Tests` — unit tests (`LifeDrop.CI.slnf`).

## Mobile (.NET MAUI, Android)
- `src/LifeDrop.Mobile.Auth` — login/session. `src/LifeDrop.Mobile.Visits` — testable logic: `VisitsApiClient`, `DailyPlan`, `VisitLifecycle`, `Tracking/` (ShiftTracker, PointQueue, ShiftApiClient), `Offline/` (Outbox, FileDurableStore).
- `src/LifeDrop.Mobile` — MAUI pages (`HomePage`, `DailyPlanPage`, `VisitDetailsPage`), `TrackingHost.cs` (shared services), `Platforms/Android/TrackingService.cs`, `AndroidMonotonicClock.cs`.

## Web panel
- `src/LifeDrop.Web` — Vite + React + TS strict + Tailwind v4; `src/api/client.ts` (auth), `src/api/admin.ts` (management calls, `can()`), `src/pages/` (Users, Devices, Customers), tests `*.test.ts(x)`.

## CI
- `.github/workflows/build.yml` (jobs: build, mobile-android, web), `database-tests.yml` (`MIN_EXPECTED_TESTS`).
