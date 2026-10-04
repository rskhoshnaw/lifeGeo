# Coding Conventions

Updated: 2026-10-04. These rules describe how code in this repository is written today and how new code must be written. Where a rule names a library that is not yet installed, it is marked **(target)**; do not add that library unless a plan step in `docs/IMPLEMENTATION-PLAN.md` calls for it.

## 1. General
- Keep each change to one plan step. Preserve existing work; do not rewrite whole files to insert a change.
- Code, file names and identifiers are English. Explanations to the team are Persian.
- Prefer simple, readable code over abstractions. No magic strings or numbers: permission keys, audit actions, status values and limits live in named constants.
- Never write passwords, tokens, device secrets, precise coordinates or personal data to logs, exceptions, test output or documentation.

## 2. C# (.NET 10 backend and MAUI)
- File-scoped namespaces; nullable reference types enabled; `record` for DTOs and value objects.
- Every async method accepts and passes on a `CancellationToken`.
- Strongly typed IDs (`TenantId`, `UserId`, `CustomerId`, `VisitId`) instead of raw `Guid` in Domain and Application.
- Domain is pure: no ASP.NET, Npgsql or I/O. Invalid state is rejected in the domain (for example `Visit.Create` requires a schedule date); the visit state machine is the only way to change visit status.
- Expected failures (validation, permission, not found, conflict) are returned as typed results, not thrown. Exceptions are for unexpected faults.
- Validation: explicit checks in the application service today; FluentValidation **(target)**. CQRS/MediatR **(target)**; current use cases are plain application services.

## 3. API
- Minimal API endpoints in `src/LifeDrop.API`. Tenant and actor always come from the validated token, never from the request body.
- Status codes (PRD §11): 400 invalid input, 401 invalid session, 403 missing permission, 404 not found within the caller's scope (including other tenants), 409 state conflict or duplicate. A predictable error must never return 500. An empty list is 200 with `[]`.
- Error body: one shared ProblemDetails shape with a stable `code` field (step 0.5). New endpoints must use it once it exists.
- Permission keys are `resource.action` (`customers.read`, `visits.fail`). Audit actions are `entity.past_tense` (`customer.created`, `device.blocked`).
- State-changing visit, check-in/out, order and payment endpoints accept an idempotency key; a retried request must not create a second record or transition.
- Check-in and check-out decisions are made on the server with fresh, valid GPS inside the customer geofence (decision D2).
- Every new or changed route is recorded in the OpenAPI contract and in `docs/phase0-api-access-matrix.md` (roles, tenant/region/owner scope, errors, audit).

## 4. Database
- PostgreSQL + PostGIS with Npgsql and parameterised SQL. EF Core **(target, not adopted)**.
- Schema changes only through numbered files in `db/migrations/` (`0007_<name>.sql`, …). A migration that has been applied anywhere is never edited; fix forward with a new one.
- Every tenant-owned table has `tenant_id`, composite foreign keys on `(tenant_id, id)`, and `ENABLE` + `FORCE ROW LEVEL SECURITY` with the `app.current_tenant` policy. The API connects with the restricted `lifedrop_app` role (no SUPERUSER/BYPASSRLS).
- Coordinates use `geography(Point, 4326)`; distance checks use `ST_DWithin`. Times are stored as `timestamptz` in UTC; "today" is computed in `Asia/Baghdad`.
- `audit_logs` is append-only for the application role (no UPDATE/DELETE grant).
- Region and team scope (decision D11) are enforced in queries, not only in the UI.

## 5. Tests
- xUnit, Arrange-Act-Assert, one behaviour per test, names `Method_Condition_Result`.
- Every endpoint test covers: allowed, wrong role, other tenant, other region, other owner (PRD §3), plus invalid input and repeated request where relevant.
- Database tests run only against an explicit disposable `TEST_DATABASE_URL` (`lifedrop_test_*`), never `lifedrop_dev`. A skipped test is not a passed test; CI must fail when expected tests are skipped.
- Use fixed values in tests (for example a fixed `DateOnly` schedule date), not `DateTime.Now`.
- A green build is not product evidence: user-facing steps also need a real API/database/audit run and, for mobile, a real Android device.

## 6. Web panel — React + TypeScript (decision D10)
- Project `src/LifeDrop.Web` (step 1.16): Vite, React, TypeScript with `strict`; no `any` without a comment explaining why.
- One typed API client generated from the backend OpenAPI contract; no hand-written duplicate DTOs.
- Access tokens are kept in memory only and never logged; hiding a button is never a substitute for a server permission check.
- All UI text through the i18n layer: `ckb` (Sorani Kurdish), `ar`, `fa` (RTL) and `en` (LTR). Use CSS logical properties so layouts work in both directions.
- Lint, typecheck and tests run in CI for every PR that changes the panel.

## 7. Mobile — .NET MAUI (Android first)
- Credentials and device secret only in platform secure storage (`ISecureCredentialStore`).
- Disable the action button while a request is in flight; the server still enforces state independently.
- Show the four sync states separately: saved locally, sent, accepted by server, rejected (PRD §2, §7). Never label an unconfirmed action as completed.

## 8. Git and pull requests
- One plan step per PR, with the step number in the title or description.
- Commit prefixes: `feat:`, `fix:`, `test:`, `docs:`, `ci:`, `chore:`.
- The PR description lists changed files, exact build/test results, and what remains unverified. Update `memory-bank/phaseStatus.md` when evidence changes status.
