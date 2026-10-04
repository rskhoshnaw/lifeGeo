# Active Context

Updated: 2026-10-04.

## Current task
Phase 0 Step 0.3 has a CI workflow draft in `.github/workflows/device-block-test.yml`, but GitHub Actions has not run it. The user chose a new repository. On 2026-10-04, `F:/Projects/lifeGeo` became an independent local Git root; its remote GitHub repository is still pending. See `phaseStatus.md` and `docs/ANTIGRAVITY-EXECUTION.md`.

## Relevant state
- Backend auth/device endpoints exist.
- `LifeDrop.Domain` models `Planned` -> `EnRoute` -> `Arrived` -> `InVisit` -> `Completed` with strongly typed `VisitId` and `CustomerId`. Product decision: `Cancelled` and `Failed` are each allowed from all four active states with a meaningful reason; role authorization remains a server-side task.
- `db/migrations/0004_customers.sql` and its repeatable RLS smoke test passed on a disposable PostGIS 16 database. The customer migration and role separation have now been successfully applied to the development database (`lifedrop_dev`). The API currently runs securely using a non-BYPASSRLS role (`lifedrop_app`) and enforces transaction-local tenant context for customer access.
- `LifeDrop.Mobile.Auth` (shared library) and `LifeDrop.Mobile.Auth.Android` (platform secure storage adapter) are fully implemented, hardened, and unit-tested in `net10.0` and `net10.0-android`.
- A standalone development harness `LifeDrop.Mobile.Auth.Android.RegistrationHarness` successfully verified the physical device registration, login, refresh, and logout workflows against the live backend API directly on an attached Android device.
- The production MAUI app (`src/LifeDrop.Mobile`) utilizes minimal Dependency Injection, successfully routing authenticated users directly to a native `HomePage` bypassing the login screen on launch via `AuthSession` validation. It natively handles device registration, login, and secure server logout routing.
- The root `README.md` is still template text; `docs/05-FOLDER-STRUCTURE.md` is planned layout, not a current inventory.

## Next actions
1. Connect the independent `lifeGeo` Git root to a new private LifeDrop GitHub repository, publish reviewed files, and verify a real workflow run. Do not publish into the unrelated parent repository.
2. Continue the remaining Phase 0 access/API/migration/security gates after CI evidence; track the earlier local build failure separately as environment-specific and undiagnosed.
3. Follow the Phase 0/1 dependency order in `docs/ANTIGRAVITY-EXECUTION.md`.

Update this file after each meaningful change, and move durable facts to progress.md, systemPatterns.md, or techContext.md.

## Recent Changes
- Implemented server-side DB permission checks for customer endpoints instead of relying on missing JWT claims.
- POST /customers now returns a created ID and Location header.
- Fixed database test project to dynamically skip if TEST_DATABASE_URL is missing, rather than reporting a false PASS.
- Successfully ran isolated database tests (migration 0004) under a non-superuser role.
- Verified human-run customer API deployment on `lifedrop_dev`: `lifedrop_app` created securely, API started without SUPERUSER/BYPASSRLS, synthetic POST/GET customer endpoints verified, and DevBootstrap config strictly separated to `ConnectionStrings:Bootstrap`.
