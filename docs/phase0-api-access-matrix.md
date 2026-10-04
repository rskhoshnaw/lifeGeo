# LifeDrop Phase 0 API access and contract inventory

Date: 2026-10-03. Scope: routes currently mapped in `src/LifeDrop.API/Program.cs` for authentication, devices, role assignment, and customers. This is a source review, not a runtime test or an approved product permission policy. Visit routes belong to Phase 1. Product target: `docs/سند نیازمندی محصول LifeDrop.docx`, sections 3, 4, 11, and 14.

| Route | Current identity and scope | Input; observed response contract | Audit in source | Test evidence in source |
| --- | --- | --- | --- | --- |
| `POST /auth/login` | Anonymous; tenant code and email/password lookup. Device ID and secret required when assigned role requires binding. | `TenantCode`, `Email`, `Password`, optional `DeviceId`/`DeviceSecret`; 200 tokens, 400 invalid input or non-TLS outside Development, 401 invalid credentials, 403 `DEVICE_NOT_APPROVED`, 429 rate limit. | Login success/failure in `auth_login_audit`; device rejection in `audit_logs`. | `RoleAssignmentDatabaseTests.MigrationEnforcesPermissionsTenantBoundaryAndAudit` checks wrong tenant, bad password, binding, audit. |
| `POST /auth/refresh` | No access token required; valid refresh token, active user/tenant/session and matching approved device where bound. | `RefreshToken`, optional device credentials; 200 rotated tokens, 400 non-TLS outside Development, 401 rejected token, 429 rate limit. | Refresh and replay actions in `audit_logs`. | Same database test checks rotation, replay and bound device. |
| `POST /auth/logout` | Valid access token and active session family. | No body; 204 success; 401 inactive/invalid token. | Session revocation action in `audit_logs`. | Same database test checks logout and subsequent rejection. |
| `POST /auth/devices/register` | Anonymous request authenticates tenant code, email and password; creates device for that user/tenant. | Credentials, label, platform (`android`/`ios`), install ID, app version; 200 device ID/secret, 400 invalid fields or non-TLS outside Development, 401 invalid credentials, 409 duplicate, 429 rate limit. | Login attempt and `device.registered`. | Same database test exercises registration and pending-device denial. |
| `POST /devices/{deviceId}/approve` | Valid access token; actor needs `device.approve`, `devices.manage`, or `system.admin`; target device must be in actor's tenant and pending. | GUID path; 204 changed, 403 permission denied, 404 unavailable target, 401 invalid session. | `device.approved`. | Same database test checks own and foreign tenant device. |
| `POST /devices/{deviceId}/block` | Same actor permissions and tenant boundary; target must be pending or approved. Revokes its refresh tokens. | GUID path; 204 changed, 403 denied, 404 unavailable, 401 invalid session. | `device.blocked`. | Same database test checks blocking and token rejection. |
| `POST /users/{userId}/roles/{roleId}` | Valid access token; `user.manageroles` or `system.admin`; actor, target user and role must be active/valid within actor's tenant. | GUID paths; 204 assigned, 400 empty GUID, 403 denied, 404 missing or foreign target, 409 duplicate, 401 invalid session. | `user.role_assigned`. | Same database test checks unauthenticated, denied, foreign role, duplicate and audit. |
| `POST /customers` | Valid access token, `customers.write`, tenant from token; restricted database role required. No region or owner check beyond assigning creator as owner. | `Type`, `Code`, `Name`; 201 ID/location, 400 invalid data, 403 denied, 409 duplicate code, 503 insecure DB role, 401 invalid session. | `customer.created`. | `CustomerApi_VerifiesPermissionsAndIsolation` checks allowed/denied, duplicate and audit, using a fake session store. |
| `GET /customers` | Valid access token, `customers.read`, tenant-scoped RLS; no region or owner filter. | Optional `limit` (1–100) and paired cursor fields; 200 array (including empty), 400 invalid pagination, 403 denied, 503 insecure DB role, 401 invalid session. | No read audit found in this path. | Same customer test checks 401, 403, tenant-separated results, empty list and invalid limit. |
| `GET /customers/{id}` | Valid access token, `customers.read`, tenant-scoped RLS; no region or owner filter. | GUID path; 200 customer, 404 absent/foreign, 403 denied, 503 insecure DB role, 401 invalid session. | No read audit found in this path. | Same customer test checks own customer and foreign-tenant 404. |

## Phase 0 gaps to resolve

1. **Product access matrix:** The PRD requires role, tenant, region, owner and device rules per endpoint. Current permission keys implement only part of that matrix; region and owner rules are absent from these customer routes. Record a product-approved rule for each route before extending permissions. A name such as `system.admin` must not imply automatic cross-tenant access.
2. **Contract consistency:** Since step 0.5 (2026-10-04) every error response is ProblemDetails with a stable `code` (see the error contract below). Request/response shapes, idempotency and Audit effects are not yet captured in one versioned OpenAPI contract (step 0.5b, now `docs/api/openapi-v1.json`, checked by `OpenApiDocument_MatchesCommittedContract`). Idempotency and Audit effects are still described only in this table.
3. **Exit gate:** Database integration tests are outside `LifeDrop.slnx`, require explicit isolated database configuration, and were not run for this inventory. The existing build workflow runs `dotnet test` on the solution, so it does not establish that those database tests run in CI. Rehearsal source lists migrations `0001`–`0006`, but that is not evidence of a successful current run or a production decision for `0006`.
4. **Security and recovery:** Verify protected-route coverage, audit retention, restricted-role deployment, empty-database migration, backup restore and secret-safe logs in separate acceptance tasks. This inventory does not establish these gates.

## Evidence and limits

- Route behavior: `src/LifeDrop.API/Program.cs` lines 90–135 and 180–602.
- Identity, permission and audit: `src/LifeDrop.Infrastructure/Auth/PostgresLoginStore.cs`, `Auth/PostgresSessionStore.cs`, `Devices/PostgresDeviceAccessStore.cs`, `Roles/PostgresRoleAssignmentStore.cs`, `Roles/PostgresPermissionStore.cs`, and `Customers/PostgresCustomerStore.cs`.
- Test source: `tests/LifeDrop.Database.Tests/RoleAssignmentDatabaseTests.cs`, `CustomerApiIntegrationTests.cs`, `DeploymentRehearsalTests.cs`; solution membership: `LifeDrop.slnx`; CI source: `.github/workflows/build.yml`.
- The codebase-memory graph was indexed on 2026-10-01 and flags several current files as changed or untracked. All material claims above were checked against current source. No build, test, API request, database migration or device run was performed for this inventory.

## Error contract (step 0.5)

Every 4xx/5xx response is `application/problem+json` (RFC 9457) with `type`, `title`, `status`, `code` and `traceId`. Clients must branch on `code`.

| Status | Code | Produced by |
| --- | --- | --- |
| 400 | `BAD_REQUEST` | Invalid input or non-TLS request outside Development |
| 400 | `INVALID_DATA` | Visit fail without a meaningful reason |
| 400 | `INVALID_STATE` | Visit transition not allowed from the current status |
| 401 | `UNAUTHENTICATED` | Missing, invalid or revoked session; failed login |
| 403 | `FORBIDDEN` | Missing permission or not the owner |
| 403 | `DEVICE_NOT_APPROVED` | Login from an unapproved or unknown device (read by the mobile client) |
| 404 | `NOT_FOUND` | Record absent or outside the caller's tenant/scope |
| 404 | `USER_NOT_FOUND`, `CUSTOMER_NOT_FOUND` | `POST /visits` with an unknown assignee or customer; `USER_NOT_FOUND` also for team membership |
| 404 | `REGION_NOT_FOUND` | Team create or move with a region outside the caller's tenant |
| 409 | `CONFLICT` | Duplicate device registration or role assignment |
| 409 | `DUPLICATE_CODE` | Customer code already used in the tenant |
| 409 | `DUPLICATE_NAME` | Region or team name already used in the tenant |
| 409 | `CONCURRENCY_CONFLICT` | Visit changed by another request between read and update |
| 429 | `TOO_MANY_REQUESTS` | Rate limit |
| 503 | `SERVICE_UNAVAILABLE` | API running with an insecure database role |
| 500 | `UNEXPECTED_ERROR` | Unhandled fault; must not occur for predictable errors |

Changed in step 0.5: the visit endpoints previously returned `{ "error": "InvalidState" }`, `"InvalidData"`, `"ConcurrencyConflict"`, `"UserNotFound"`, `"CustomerNotFound"`; these are now the UPPER_SNAKE_CASE codes above. `DEVICE_NOT_APPROVED` and `DUPLICATE_CODE` keep their values. Test evidence: `CustomerApiIntegrationTests.ApiErrors_ReturnProblemDetailsWithStableCodes`, the `DEVICE_NOT_APPROVED` body check in `RoleAssignmentDatabaseTests`, and `AuthApiClientTests.LoginAsync_DeviceNotApprovedProblemDetails_ReturnsDeviceNotApprovedStatus`.

## Regions and teams (step 0.6a, decision D11)

| Route | Permission | Behaviour | Audit |
| --- | --- | --- | --- |
| `POST /regions` | `regions.manage` | 201 `{id}`; name 1–100 chars, unique per tenant (409 `DUPLICATE_NAME`) | `region.created` |
| `GET /regions` | `regions.read` | 200 array of the caller's tenant only | — |
| `POST /teams` | `teams.manage` | 201 `{id}`; region must exist in the tenant (404 `REGION_NOT_FOUND`); name unique per tenant | `team.created` |
| `GET /teams` | `teams.read` | 200 array with each team's `regionId` | — |
| `PUT /teams/{teamId}/region` | `regions.manage` | 204; moves the team to another region of the tenant | `team.region_changed` |
| `PUT /teams/{teamId}/members/{userId}` | `teams.manage` | 204; a user belongs to at most one team, so this moves the user | `team.member_assigned` |
| `DELETE /teams/{teamId}/members/{userId}` | `teams.manage` | 204; 404 if the user is not in that team | `team.member_removed` |

All tables use tenant RLS. Test: `CustomerApiIntegrationTests.RegionTeamApi_EnforcesPermissionsTenantIsolationAndAudit`. Region scope on customers and visits is step 0.6b. Deployment note: the production `lifedrop_app` role needs `SELECT, INSERT` on `regions`, `teams`, `team_members`, `UPDATE` on `teams`, `team_members` and `DELETE` on `team_members` (as in `DeploymentRehearsalTests`); this is part of step 0.9.

## Data scope (step 0.6b)

`roles.data_scope` (migration 0008): `tenant` (default, all rows), `region` (region of the user's team), `own` (owned/assigned only). A user's scope for a permission is the widest among roles granting it.

| Route | `tenant` | `region` | `own` |
| --- | --- | --- | --- |
| `GET /customers`, `GET /customers/{id}` | all customers | customers in the team's region + customers owned by the user | customers owned by the user |
| `POST /visits` | any user, any customer | assignee in the same region (or self); customer visible in region scope | only self; only owned customers |

Out-of-scope customers return 404 `NOT_FOUND` / `CUSTOMER_NOT_FOUND`; an out-of-region assignee returns 403. Test: `DataScope_LimitsCustomerReadsAndVisitAssignment`.

## Default roles (step 0.6c)

`seed_default_roles(tenant_id)` (migration 0009) creates these roles for a tenant and is run once for every existing tenant. Re-running only adds missing roles/grants; it never changes or removes existing ones. Note: if a tenant already has a role with one of these names, the default grants are added to it.

| Role | Scope | Device binding | Permissions |
| --- | --- | --- | --- |
| CompanyManager | tenant | no | customers.read, regions.read, teams.read |
| SalesManager | tenant | no | customers.read/write, visits.write, regions.read/manage, teams.read/manage, device.approve, user.manageroles, sessions.kill |
| Supervisor | region | no | customers.read/write, visits.write, regions.read, teams.read |
| FinanceManager | tenant | no | customers.read |
| SalesRep | own | yes | customers.read/write, visits.fail |
| ScientificRep | own | yes | customers.read, visits.fail |
| WarehouseManager, Driver | own | yes | none yet (phase 2) |

Super Admin is cross-tenant and not seeded (phase 3). Test: `DefaultRoles_SeedIsIdempotentAndEnforcedByApi`.

## Kill Session (step 0.7)

`POST /users/{userId}/sessions/revoke` — permission `sessions.kill`; scope tenant = any user, region = users in the actor's region, own = only self. Revokes every active refresh-token family of the target; because each request checks its session family, the target's access tokens stop working on the next request (401), without reinstalling the app. 204 on success, 403 out of scope, 404 `USER_NOT_FOUND`. Audit `auth.sessions_killed` (actor = administrator, entity = target user), written even when no session was active. Test: `KillSessions_RevokesTargetImmediatelyWithinScopeAndAudits`.
