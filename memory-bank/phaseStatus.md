# LifeDrop phase status

Updated: 2026-10-04. Source of product requirements: `docs/سند نیازمندی محصول LifeDrop.docx` (version 2.0, especially sections 14 and 5-7). `docs/PRD.md` is an older execution plan, not the primary product PRD. This file tracks the ASP.NET Core/.NET MAUI checkout at `F:/Projects/lifeGeo` only.

Status words: **present** = observed in current source; **reported test** = Antigravity output pasted into the planning chat; **verified** = independently checked in the named environment; **open** = no sufficient evidence of completion. Never infer a production deployment from isolated tests.

## Phase 0 — Foundation (open)
- Present: ASP.NET Core API, PostgreSQL migrations 0001-0006, tenant-aware authentication and role/device endpoints, customer API, MAUI authentication UI.
- Earlier evidence: customer API on `lifedrop_dev` and Android authentication on a physical device; see `progress.md` for scope. Recheck before reuse.
- Step 0.2 source: `RoleAssignmentDatabaseTests.cs` asserts device-block 403 for an actor without permission, 404 for a foreign-Tenant device, unchanged foreign-device `pending` status, and no block audit before an authorized block; the existing final audit assertion expects one permitted block. Antigravity reported a focused test result of 1 passed, 0 failed, 0 skipped on a disposable PostGIS database and container cleanup. Its `task-928.log` independently shows a successful build with 9 warnings; the later `--no-restore` 0-warning build, raw test output, and cleanup output were not available in the inspected logs. A separate local build had exited 1 with zero displayed errors; its environmental trigger remains unknown. See `docs/ANTIGRAVITY-EXECUTION.md`.
- Step 0.3 source: `.github/workflows/device-block-test.yml` is a CI draft with a PostGIS service, explicit .NET 10 setup, a restricted `lifedrop_test_*` connection, an exact one-test TRX count check, and an always-uploaded TRX artifact. Its YAML parsed locally. Antigravity reported a successful local Docker rehearsal and schema cleanup, but raw rehearsal logs were not available here. Git root is `F:/Projects`, `lifeGeo/` is untracked, and no remote is configured; GitHub Actions has **not** run this workflow.
- Open: complete permission matrix (role, region, owner, device), visit audit, current API contract, migration 0006 deployment decision, isolated end-to-end migration gate, CI, and recovery/security checks. On 2026-10-04 an independent local Git repository was initialized at `F:/Projects/lifeGeo`; no remote is connected and no GitHub Actions run is verified. Preserve files and backups.

## Phase 1 — Operational visit MVP (partial)
- Present in source: visit create, assigned daily list, `Planned -> EnRoute -> Arrived -> InVisit -> Completed`, and `Failed` endpoint with a reason and `visits.fail` permission. Conditional status update checks expected status and assigned user.
- Antigravity reported focused application/PostGIS tests for the visit slices; these are not a full phase exit gate. On current inspection, `VisitApi_Failed_EnforcesPermissionsAndTransitions` is absent from `CustomerApiIntegrationTests.cs`; verify/fix this before extending the workflow. Earlier unrelated cross-tenant assertions were changed by a broad replacement and subsequently restored in current source.
- Product decision: assigned employee may report `Failed`; manager may mark `Cancelled`. Deployment grants for `visits.fail` are pending.
- Critical PRD gap: `/start` and `/complete` currently change status without the required server-side device/GPS/geofence check, and `/complete` has no persisted valid visit report prerequisite. Do not call them PRD-complete check-in/check-out.
- Other open steps: scheduling fields (visit type, priority, time), reassignment with audit, region authorization, cancellation with manager permission/reason/audit, visit report, customer location controls, Android visit screens, shifts/tracking, offline queue, operations panel, and a real device/API/DB/audit end-to-end visit on isolated data.

## Phase 2 — Sales and follow-up (open)
Product catalog/pricing; order; warehouse/delivery; payment/sample distribution; follow-up; reports/alerts; extended offline behavior. Require independent role/region/tenant and real data tests.

## Phase 3 — Scale and automation (open)
Commercial SaaS, advanced forms/reporting, routing/AI/integrations, performance and backup-restore gates. iOS runtime verification needs an Apple environment; plan it separately from Android proof.

## Next safe step
Phase 0 Step 0.3 is **CI PREPARED — NOT RUN; remote pending**. The user chose a new repository. A local Git root now exists at `F:/Projects/lifeGeo`, and the workflow is at this root's `.github/workflows/`. The remote GitHub repository still needs creation/connection and an observed CI run. Do not push into the unrelated `F:/Projects` parent. Keep the earlier local build failure as an environment-specific unresolved observation; do not attribute it to CS8605 without evidence. The Phase 1 `Failed` integration-test gap remains queued. Preserve existing work. Prompt method and phase order: `docs/ANTIGRAVITY-EXECUTION.md`.
