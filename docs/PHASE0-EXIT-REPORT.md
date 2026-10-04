# Phase 0 exit report

Date: 2026-10-04. Plan step 0.10. Exit criteria from PRD §14: login/logout, tenant and role limits, migrations from an empty database, and base tests pass in named environments.

## Evidence

All database tests run in CI (`database-tests` job) on a disposable PostGIS 16 container. Every run applies migrations 0001–0009 to an empty schema, uses a restricted `test_user` plus a superuser URL, and fails if any test is skipped or fewer than `MIN_EXPECTED_TESTS` run. Last green run: PR #6, head `a42b2f1`, run 37214431821 (23 tests); `build` run 37214431820 (Domain 107, Application 37, Mobile.Auth 85).

| Exit criterion | Evidence (test) | Status |
| --- | --- | --- |
| Login, refresh rotation, logout | `RoleAssignmentDatabaseTests.MigrationEnforcesPermissionsTenantBoundaryAndAudit` | ✅ CI |
| Device binding, approval, block | same test; `DEVICE_NOT_APPROVED` body check | ✅ CI |
| Kill Session takes effect on the next request | `KillSessions_RevokesTargetImmediatelyWithinScopeAndAudits` | ✅ CI |
| Tenant isolation (RLS + explicit filters) | customer, visit, region/team, audit tests (other-tenant 404) | ✅ CI |
| Role permissions and data scope (tenant/region/own) | `DataScope_LimitsCustomerReadsAndVisitAssignment`, `RegionTeamApi_…`, `VisitApi_Failed_…` | ✅ CI |
| Default roles per tenant | `DefaultRoles_SeedIsIdempotentAndEnforcedByApi` | ✅ CI |
| Audit of sensitive actions; append-only; searchable | audit-row assertions in each test; `AuditApi_SearchesWithinScopeAndRowsCannotBeChanged` | ✅ CI |
| Migrations from an empty database | every CI run; `CustomerApi_SchemaCleanedUpOnMigrationFailure` | ✅ CI |
| Uniform error contract; no predictable 500 | `ApiErrors_ReturnProblemDetailsWithStableCodes` | ✅ CI |
| Versioned API contract | `OpenApiDocument_MatchesCommittedContract` | ✅ CI |
| Least-privilege runtime role | `DeploymentRehearsalTests` using `db/grants/lifedrop_app_grants.sql` | ✅ CI |
| Every non-public route authenticated | `EveryRouteRequiresAuthorizationExceptReviewedPublicRoutes` | ✅ CI |
| No secrets in logs | log scan in `Rehearse_LeastPrivilege_Deployment` | ✅ CI |

Each new check was also shown to fail when the behaviour it guards was removed (mutation checks listed in PRs #5 and #6).

## Open items (do not block Phase 1 code; block a Phase 0 sign-off on `lifedrop_dev`)

1. **Owner action:** on `lifedrop_dev`, apply migrations 0006–0009 and `db/grants/lifedrop_app_grants.sql`. The deployed grant script lacks `visits`, `regions`, `teams`, `team_members`.
2. Super Admin cross-tenant access is not implemented (phase 3).
3. `auth_login_audit` is recorded but not searchable through the API.
4. Backup/restore and RPO/RTO (D4, D9) are phase 3.

## Verdict

Phase 0 is **complete in code and CI**. It is complete on `lifedrop_dev` once open item 1 is applied and the API is smoke-tested there.
