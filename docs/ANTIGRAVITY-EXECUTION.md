# LifeDrop execution guide for Antigravity

Updated: 2026-10-04. Use this with `memory-bank/phaseStatus.md`; the product authority is `docs/سند نیازمندی محصول LifeDrop.docx` v2.0. This guide controls work in `F:/Projects/lifeGeo`, not the separate `life-drop` checkout. Source observations, reported test output, independently verified runtime, and open work must remain separate.

## Fast, evidence-based loop

1. **Inspect first:** read the small Memory Bank files, then use codebase-memory for structural questions. Check index coverage for each relied-on code file; read current source where the graph is stale. Use exact file references, not a repository-wide scan.
2. **Plan second:** choose one PRD requirement and one small change. State exact files, acceptance criteria, test environment, and stop point. Do not introduce a dependency, plugin, or broad refactor unless the task demonstrably requires it.
3. **Implement third:** preserve uncommitted work and backups. Make a narrow edit; do not rewrite whole files to insert a test. Use a disposable `lifedrop_test_*` database for write tests, with an explicit connection set before app initialization. Never fall back to `lifedrop_dev`.
4. **Verify:** record build, focused test, real API/database/audit behavior, and device behavior as separate evidence. `Skipped` is not `Passed`; a green build is not a product test. For user-facing flows, require a real UI/API path before calling the slice complete.
5. **Report and stop:** report changed files, exact outcomes, remaining unverified items, and the next smallest dependency. Update `memory-bank/phaseStatus.md` only when new evidence changes status.

Use workspace Rules for stable constraints; use a small Agent Skill only if the same multistep process repeats enough to justify it. Antigravity's official guide recommends on-demand Skills to limit context, and says legacy Workflows retire on 2026-11-01. No new plugin or skill is required for the current gate.

## Delivery order and exit gates

| Phase | Work order | Exit evidence |
| --- | --- | --- |
| 0 — Foundation | Auth/device/Tenant/RBAC audit and API contract; isolated empty-database migrations; CI and basic security/recovery checks | Login/logout, role and Tenant denial, migrations from empty database, and base tests pass in named environments. |
| 1 — Operational visit MVP | Daily plan/assignment, valid report, server GPS/geofence, Android visit UI, shift/tracking, truthful basic offline state, operations panel | One test visit from creation to completion on a device and isolated environment, with API, database, audit, error, and retry evidence. |
| 2 — Sales/follow-up | Product/pricing, order, warehouse/delivery, payment/sample, follow-up and reports | Real order-to-delivery and financial flow with role, region, and Tenant checks. |
| 3 — Scale/automation | Commercial SaaS, advanced forms/reports/routing/AI, performance and backup recovery | Security, capacity, and restore tests in the target environment; iOS runtime requires an Apple environment. |

This order is from PRD section 14. Do not call Phase 1 check-in/check-out complete while `/start` and `/complete` lack server-side device/GPS/geofence checks and a persisted valid report prerequisite.

## Current evidence: Phase 0, Step 0.2

`tests/LifeDrop.Database.Tests/RoleAssignmentDatabaseTests.cs` contains assertions for unauthorized block = 403, foreign-Tenant block = 404, foreign device still `pending`, and zero `device.blocked` audit rows before the permitted block. Antigravity reports a focused result of 1 passed, 0 failed, 0 skipped on disposable PostGIS, plus cleanup. Its `task-928.log` confirms a successful build with 9 warnings; a later incremental build reportedly had 0 warnings. The inspected logs did not contain raw test output or cleanup output, so these remain reported evidence. A separate local `dotnet build --no-restore` exited 1 with zero displayed errors; diagnostic output reaches a failed `_GetProjectReferenceTargetFrameworkProperties` target without a proven root cause. Neither SDK resolver messages nor the earlier CS8605 warning establish that cause.

Phase 0 Step 0.3: `.github/workflows/device-block-test.yml` is prepared. It defines a disposable PostGIS service, explicit .NET 10, a restricted `lifedrop_test_*` database, an exact one-test TRX check, and an always-uploaded artifact. YAML parsing passed locally; Antigravity reported a successful Docker rehearsal and schema cleanup without raw logs available here. The user chose a new repository, and an independent local Git root was initialized at `F:/Projects/lifeGeo` on 2026-10-04. **CI has not run:** no GitHub remote is connected. Create/connect the private remote and verify an actual workflow run before marking this gate complete. Never publish into the unrelated `F:/Projects` parent repository.

## Prompt shape

Every Antigravity execution prompt begins with the user's five English preparation rules: read `memory-bank`; use codebase-memory; avoid full-file reads; mention only necessary `@` sections; do not rescan the project. Then provide, in order: phase/step, one goal, exact scope, acceptance checks, test environment, safety boundary, and stop point. Keep these task-specific lines short; refer to this guide instead of pasting the roadmap again.

## External references used for process, not project status

- [Google Antigravity Rules](https://www.antigravity.google/docs/rules-workflows?tab=ide) and [Skills migration](https://antigravity.google/docs/migration/workflows-to-skills): stable rules versus on-demand skills; workflow retirement date. Verify the installed Antigravity version before using product features.
- [Google Antigravity Skills codelab](https://codelabs.developers.google.com/getting-started-with-antigravity-skills?hl=en): selective context loading and tool-bloat guidance.
- [Microsoft MSBuild logs](https://learn.microsoft.com/en-us/visualstudio/msbuild/obtaining-build-logs-with-msbuild?view=vs-2022) and [ASP.NET Core integration tests](https://learn.microsoft.com/en-us/aspnet/core/testing/integration-testing): targeted diagnostics and meaningful host/infrastructure tests.
- [PostgreSQL RLS documentation](https://www.postgresql.org/docs/16/ddl-rowsecurity.html): test database permissions with a restricted role; owners and privileged roles may bypass RLS.
- Social discussions are anecdotal prompts for investigation, never proof of LifeDrop behavior or a reason to install more tooling.
