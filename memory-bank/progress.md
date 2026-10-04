# Progress

Updated: 2026-10-04. This is a repository snapshot plus explicitly identified earlier runtime evidence, not a blanket completion claim.

## Present in source
- .NET 10 Domain, Application, Infrastructure, API, DevBootstrap, Mobile.Auth, and Mobile.Auth.Tests projects.
- Role assignment, tenant-aware login/JWT, refresh-token rotation, device registration/approval/blocking, and logout endpoints.
- Mobile HTTP Auth client with JSON mapping and device registration/login/refresh/logout contracts.
- SQL migrations `0001_role_assignment.sql` through `0005_dev_admin_customer_permissions.sql`; `0004` and `0005` have been successfully applied to the development database.
- Domain, Application, and Mobile.Auth xUnit projects. A database integration test project exists separately from `LifeDrop.slnx` and requires `TEST_DATABASE_URL`.
- Development bootstrap supports local admin and field-user credentials; do not record credential values here.

## Verified in current slice
- Customer Integration Tests (CustomersDatabaseTests) successfully ran against an isolated PostGIS 16 instance. Migration 0004 applied and verified under a non-superuser role (test_user). Verified creation, conflict handling, and strict tenant isolation.
- On 2026-10-03, migrations `0001`-`0004` applied successfully in a disposable, network-isolated PostGIS 16 container. `db/tests/0004_customers_rls_smoke.sql` passed owner-role RLS checks for absent tenant context, tenant A/B reads, blocked cross-tenant update and insert. Probe rows and role rolled back; the container was removed. This does not verify application integration or the development database.
- On 2026-10-03, `02_apply_changes.sql` was verified committed on the development database (`lifedrop_dev`), creating and enabling the restricted `lifedrop_app` role. The API successfully started using `lifedrop_app` (logging no SUPERUSER/BYPASSRLS). A synthetic dev admin login succeeded, `POST /customers` returned 201, and `GET /customers/{id}` returned the synthetic code/name and `no_location`. After a later `lifedrop_app` password reset, the API again started with the restricted role and its root endpoint returned running (customer routes were not retested after this reset). `LifeDrop.DevBootstrap` was successfully separated to read `ConnectionStrings:Bootstrap` and builds (but was not run; `postgres` rotation status unconfirmed). This verifies development-database functionality only (no production or iOS verification).
- `LifeDrop.Domain`: Implemented the Visit state machine (`Planned → EnRoute → Arrived → InVisit → Completed`, plus `Failed` and `Cancelled`) keeping the layer pure and strictly enforcing transition rules.
- `tests/LifeDrop.Domain.Tests`: 107 tests passed, including the product-approved `Cancelled` and `Failed` origins (all four active states), invalid jumps, and missing exception reasons.
- `tests/LifeDrop.Mobile.Auth.Tests`: 84 passing unit tests verifying device registration (200, 401, 409, 400), login (200, 401, 403 DEVICE_NOT_APPROVED, 403), refresh (200, 401), logout (204, Bearer header), credential redaction, response disposal on all paths, cancellation preservation, HTTP timeout distinction, malformed 200 payload rejection, generic network exception messages, AuthSession orchestration, and strict concurrency assertions ensuring logout fails cleanly if the session is externally overwritten.

- Physical device behavior HAS been directly verified on an attached Android device. The development field-device registered, logged in, performed a time-advanced test refresh, and logged out successfully using the standalone `LifeDrop.Mobile.Auth.Android.RegistrationHarness`.
- `LifeDrop.Mobile` (the production MAUI App) builds and deploys successfully to a physical Android device (`net10.0-android`), wiring up the dependency injection for the core `ISecureCredentialStore` and `IAuthApiClient` adapters. Production authentication UI screens (Login, Home) are fully built and actively route via `AuthSession` validation logic, confirming safe native logout.

## Not yet implemented or verified
- iOS build and runtime. No Apple build environment is available to verify `maccatalyst` or `ios` target structures.
- Offline synchronization, live GPS/geofence tracking, and broader sales workflow remain future slices in this .NET checkout.
- The inherited JSX/Express template was removed on 2026-10-04. The root `README.md` is still template text; do not infer product behavior from it. `docs/PRD.md` is now the English working edition of PRD v2.0 with the decision log.

## Next milestone
Shared Android/iOS authentication client with secure storage and focused tests, followed by platform-specific verification. Treat iOS background GPS as a separate milestone.

## CI evidence (2026-10-04)
- PR #2 merged at `4cbbd1f`: `build` (build + unit tests of `LifeDrop.CI.slnf`) and `test-device-block` (one PostGIS database test) both succeeded on head `b472c5c`. Locally on .NET SDK 10.0.112: Application.Tests 37/37, Domain.Tests 107/107, Mobile.Auth.Tests 84/84. Database tests other than the device-block test are not yet run in CI (step 0.4).

## Plan
Remaining work, step order and product decisions: `docs/IMPLEMENTATION-PLAN.md`.
- Step 0.4 local evidence (2026-10-04): `LifeDrop.Database.Tests` on local PostgreSQL 16 + PostGIS 3.4 with restricted `test_user` and superuser URL: 14 passed, 0 failed, 0 skipped, three consecutive runs. Before the fix a clean run had 3 failures (`Jwt:Issuer is required`) and 3 skips.
- Step 0.4 CI evidence (2026-10-04): PR #4 job `database-tests` (run 37209788466) — 14 passed, 0 failed, 0 skipped; TRX count check passed.
- Step 1.1 local evidence (2026-10-04): new `VisitApi_Failed_EnforcesPermissionsAndTransitions` passes; full database suite 15/15, 0 skipped, two runs; removing the `visits.fail` permission check or the owner check in `MarkVisitFailed` makes the test fail.
- Step 0.5 local evidence (2026-10-04): unified ProblemDetails error contract; database tests 16/16 (two runs), Mobile.Auth 85/85 including a ProblemDetails `DEVICE_NOT_APPROVED` compatibility test. Against the pre-change `Program.cs` the two new contract assertions fail.
- Steps 1.1 and 0.5 CI evidence (2026-10-04): PR #5 job `database-tests` (run 37211924702) 16 passed, 0 skipped; `build` green.
- Step 0.5b local evidence (2026-10-04): OpenAPI contract snapshot test passes; altering one documented status makes it fail; database tests 17/17 twice.
