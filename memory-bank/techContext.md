# Technical Context

## Current checkout
- Workspace: `F:\Projects\lifeGeo` (separate from `F:\Projects\life-drop`).
- CI (`.github/workflows/build.yml`) builds and unit-tests `LifeDrop.CI.slnf` on .NET 10; `database-tests.yml` (step 0.4) runs all `LifeDrop.Database.Tests` on a disposable PostGIS container with a restricted `test_user` and a superuser URL, and fails if any test is skipped or fewer than `MIN_EXPECTED_TESTS` run. It replaced `device-block-test.yml` (one test), which was green on PR #2.
- `LifeDrop.slnx` targets .NET 10 and lists Domain, Application, Infrastructure, API, DevBootstrap, Domain.Tests, Application.Tests, Mobile.Auth.Tests, Mobile.Auth, and the Android MAUI app. `tests/LifeDrop.Database.Tests` exists but is not listed in the solution; run it by project path when needed.
- API is ASP.NET Core with Npgsql/PostgreSQL. Migrations are plain SQL under `db/migrations/`. The development HTTP launch profile uses `http://localhost:5256`; the root route is a service status endpoint and OpenAPI is exposed in Development.
- Required API configuration: `ConnectionStrings:LifeDrop`, `Jwt:Issuer`, `Jwt:Audience`, and a `Jwt:SigningKey` of at least 32 UTF-8 bytes. Keep real values in ignored local configuration or secret storage, never in this folder.
- Database integration tests require an explicit isolated `TEST_DATABASE_URL`. They create/write schema and must never fall back to the development connection.

## Implemented versus planned
- The Android MAUI app is present in `src/LifeDrop.Mobile` with `net10.0-android` in its project file. Earlier physical Android authentication evidence is in `progress.md`; recheck the host SDK/device before further claims. iOS runtime verification still requires an Apple environment. The web panel will be a new React + TypeScript project at `src/LifeDrop.Web` (decision D10, step 1.16); it does not exist yet. Root-level JSX/Express files are inherited template code, not the panel.
- PostgreSQL/PostGIS migration and isolated test evidence exists; see `progress.md` for exact scope. Redis, full visit/offline flows, and production deployment remain unverified or planned.
- On 2026-10-03, a local build of `tests/LifeDrop.Database.Tests` exited 1 with zero displayed errors. Diagnostic output shows a failed `_GetProjectReferenceTargetFrameworkProperties` target, without a confirmed root cause. Do not call the SDK resolver messages alone the cause. Docker access in that session was denied.
- A later Antigravity `task-928.log` shows this project built successfully with 9 warnings; Antigravity also reports a later 0-warning incremental build and 1/1 focused database test pass. The inspected task logs did not include raw test or cleanup output. Keep those evidence levels separate.

## Local navigation
- Product authority: `docs/سند نیازمندی محصول LifeDrop.docx` v2.0. Execution method: `docs/ANTIGRAVITY-EXECUTION.md`. Numbered architecture docs contain target conventions; verify against source before treating them as installed.
- Current auth contracts: `src/LifeDrop.Application/Auth/` and `src/LifeDrop.Application/Devices/`.
- Never inspect or copy contents of ignored `artifacts/` or local settings into the Memory Bank; they can hold secrets.
