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
| `POST /users` | Valid access token; `user.manageroles` or `system.admin`; the account is created in the actor's own company with one role of that company. | Body `{email, password, roleId}`; e-mail plain and ≤200 characters (lower-cased), password 12-200 characters (stored hashed, PBKDF2), role required; 201 `{id}`, 400 invalid data, 403 denied, 404 role not in this company, 409 e-mail already used in this company. One transaction writes the user, the role and the audit row `user.created`. Tests: `CreateStaffUserTests`, `UserCreationStoreTests`. |
| `PUT /users/{id}/email`, `PUT /users/{id}/password`, `POST /users/{id}/deactivate`, `POST /users/{id}/activate` | Valid access token; `user.manageroles` or `system.admin`; only users of the actor's own company. | Email body `{email}` (plain, ≤200, lower-cased) / password body `{password}` (12-200, hashed); 204 done, 400 invalid, 403 denied, 404 user not in this company, 409 e-mail taken or deactivating oneself. Password change and deactivation revoke all refresh tokens of the user. | `user.email_changed`, `user.password_changed`, `user.deactivated`, `user.activated`. | `ManageUsersTests`, `UserAccountStoreTests`. |
| `PUT /users/{id}/city` | `user.manageroles` or `system.admin`, users of the actor's company only | Body `{city}` (empty/null clears; ≤80); 204, 400, 403, 404. `GET /users` also returns `city` and `phoneType` (platform of the newest registered phone) | `user.city_changed` |
| `PUT /me/password` | Valid access token (any user, the login rate limit applies) | Body `{currentPassword, newPassword}`: the current one must be right (400 `WRONG_PASSWORD`), the new one 12-200 characters and different (400 `INVALID_DATA`); 204; every other session of the user ends, the one making the change stays | `user.password_changed_self` |
| `GET /permissions` | `user.manageroles` or `system.admin` | The permission keys a role can hold (all keys except `system.admin`), with their group | — |
| `GET /roles` | same | Also returns each role's `permissions`, `userCount`, `isDefault` and `requiresDeviceBinding` | — |
| `POST /roles` | same | Body `{name ≤60, dataScope tenant|region|own, requiresDeviceBinding, permissions[]}` (known keys only); 201 `{id}`, 400, 403, 409 name taken | `role.created` |
| `PUT /roles/{id}` | same | Same body, replaces name, reach and all permissions; a default role keeps its name (400); 404; 409 when the name is taken or nobody active would be left able to manage roles (nothing changes) | `role.updated` |
| `DELETE /roles/{id}` | same | 204; 404; 409 for a default role, a role somebody has, or when it was the last that can manage roles | `role.deleted` |
| `DELETE /users/{id}/roles/{roleId}` | same | Removes that one role; 204; 409 when the target is the caller (nobody takes a role from themselves), 404 when the user lacks it or is in another company | `user.role_removed` |
| `PUT /customers/{customerId}` | Valid access token; `customers.write` within the actor's data scope. | Body `{type, code, name}` (all required after trimming; ≤40/≤50/≤200); 204, 400 invalid, 403 denied, 404 not in scope or company, 409 code used by another customer. | `customer.edited`. | `CustomerEditServiceTests`, `CustomerArchiveStoreTests`. |
| `PUT /customers/{customerId}/contact` | Valid access token; `customers.write` within the actor's data scope. | Body `{contactName, phone2, notes}` (each optional after trimming, empty clears; ≤100/≤40/≤500); 204, 400 too long, 403 denied, 404 not in scope or company. | `customer.contact_changed`. | `CustomerEditServiceTests`. |
| `PUT /customers/{customerId}/doctor` | Valid access token; `customers.write` within the actor's data scope. | Body `{clinicName, specialty, cooperates, dealer, customerClass}` (all optional; null/empty clears; specialty one of orthopedics, neuro_spine, pediatrics, women_obstetrics, surgery, kidney_urology, ent, other; class A+, A, B or C; clinic ≤150); 204, 400 unknown value, 403 denied, 404 not in scope or company. | `customer.doctor_changed`. | `CustomerEditServiceTests`. |
| (rule) customers of type Doctor | Permission `customers.doctors.read` (CompanyManager, SalesManager, Supervisor, FinanceManager, ScientificRep; **not** SalesRep) | Without it every customer read (`GET /customers`, search, `/customers/map`, `GET/PUT /customers/{id}…`), visit-plan candidates, priority changes and orders leave Doctor customers out (404 for one doctor); `POST /customers` and `PUT /customers/{id}` with type Doctor answer 403. All other customers of the region stay visible to the sales rep. | — |
| (rule) doctors only for the scientific rep | `customers.write` / `customers.location.write` with the restriction `customers.doctors.only` (ScientificRep) | Such a user can create a customer only of type Doctor (else 403), retype only to Doctor (else 403) and change (edit, doctor details, location, contact) only the doctors in their own scope; every other customer is 404. Other roles are unaffected. | — |
| `GET /customers?q=` | Same as the list (`customers.read` scope). | Search text of 2-100 characters over name, code, phone and second phone (case-insensitive, `%`/`_` taken literally); 400 otherwise; archived customers are not searched. | — | `CustomerEditServiceTests`. |
| `POST /notifications/send` | Valid access token; `notifications.send` (SalesManager: whole company; Supervisor: only people of the supervisor's region); `own` scope is refused. | Body `{message (1-500), everyone? | userIds? (1-500) | role?, regionId?, teamId?}`: exactly one kind of audience (everyone, a list of people, or filters combined); 200 `{recipients}` (active people only; 0 = nobody matched), 400 invalid, 403 denied. Delivery: in-app list, app push (FCM) and Telegram, by the existing dispatchers. | `notification.broadcast` (entity = number of people). | `BroadcastServiceTests`. |
| `GET /customers?type=&placeId=&cooperates=` | Same as the list (`customers.read` scope; doctors hidden as everywhere). | Plan step 5.2: any of `type` (text, 40 characters at most, case-insensitive), `placeId` (a city or neighbourhood; customers placed anywhere below it) and `cooperates` (true/false, the doctor flag) narrows the active customers; `q` is optional with them (2-100 characters when given); 400 on a bad limit/cursor/text, an empty `placeId`; archived customers are not included. | — | — |
| `GET /customers/{customerId}/history` | Valid access token; `customers.read` with the customer inside the actor's data scope (else 404; a doctor is "not found" for a user who may not read doctors). The orders also need `orders.read` (otherwise `orders` is null) and follow the order rules (area, hidden prices: the total is 0 and `pricesHidden` true). | Plan step 5.4. Optional `limit` 1-100 (default 30); 400 otherwise. Newest first: the visits of the customer (status, date, visitor e-mail, accepted check-in and check-out time, report outcome and note, the reason of a failed or cancelled one) and the orders with their product lines (name, ordered and delivered quantity, never a price). Read only. | — | — |
| `GET /tracking/days?userId=&month=` | Valid access token; `tracking.read` and the user inside the reader's area (as `GET /tracking/history`). | Plan step 5.4. `month` is any date of the month (Iraq days); 400 for a month in the future or older than two years; 404 when the user is outside the area. Returns the days with a shift: date, shifts and the points still kept (0 when purged). No coordinates, so it is not audited. | — | — |
| `GET /targets?month=` | Valid access token; `targets.read` (sales manager, supervisor, company manager, finance manager); rows only for users inside the reader's data scope (the reader, the people of the reader's region, or everybody). | Plan step 5.5. `month` is any date of the month, from two years back to one year ahead (400 otherwise). Rows of visitor × product that have a target or a delivered quantity: the target (null when none) and the quantity delivered of the orders the visitor placed (`order_items.delivered_quantity` of orders in status `delivered`, counted in the Iraq month of their last `delivered` step). Read only. | — | — |
| `PUT /targets` | Valid access token; `targets.manage` (sales manager, supervisor); the visitor inside the actor's data scope (else 404). | Plan step 5.5. Body `userId`, `productId`, `month` (any date of the month) and `quantity` (more than 0 and at most 1,000,000 sets or replaces the target; `null` or 0 clears it); 204; 400 invalid data or a month outside two years back / one year ahead; 404 visitor or product unknown. | One audit row per real change: `target.set` or `target.cleared` (entity `userId:productId:yyyy-MM`). | — |
| `GET /targets/mine?month=` | Valid access token; no permission (everybody sees only their own figures). | Plan step 5.5, for the visitor's page in the app. Same rows as `GET /targets` but only for the caller (a visitor with data scope "region" does not see colleagues); the same month rules (400 otherwise). Read only. | — | — |
| `POST /customers/{customerId}/archive`, `POST /customers/{customerId}/restore` | Valid access token; `customers.approve` within the actor's data scope. | 204, 403 denied, 404 not in scope or company. An archived customer is left out of `GET /customers`, the map, new visits and automatic plans; its history stays. `GET /customers?archived=true` lists only archived ones and needs `customers.approve` as well. | `customer.archived`, `customer.restored`. | Same tests. |
| `PUT /followups/{followUpId}` | Valid access token; the creator of the follow-up or a user with `followups.manage` whose area covers the responsible user. | Body `{subject, dueDate}` (subject 1-200 after trimming; due day today or later, or unchanged); 204, 400 invalid, 403 not creator/manager, 404 not visible, 409 completed or cancelled. | `followup.edited`. | `FollowUpEditTests`. |
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
| CompanyManager | tenant | no | customers.read, regions.read, teams.read, audit.read |
| SalesManager | tenant | no | customers.read/write, visits.write, visits.cancel, regions.read/manage, teams.read/manage, device.approve, user.manageroles, sessions.kill, audit.read |
| Supervisor | region | no | customers.read/write, visits.write, visits.cancel, regions.read, teams.read |
| FinanceManager | tenant | no | customers.read, audit.read |
| SalesRep | own | yes | customers.read/write, visits.fail |
| ScientificRep | own | yes | customers.read, visits.fail |
| WarehouseManager, Driver | own | yes | none yet (phase 2) |

Super Admin is cross-tenant and not seeded (phase 3). Test: `DefaultRoles_SeedIsIdempotentAndEnforcedByApi`.

## Kill Session (step 0.7)

`POST /users/{userId}/sessions/revoke` — permission `sessions.kill`; scope tenant = any user, region = users in the actor's region, own = only self. Revokes every active refresh-token family of the target; because each request checks its session family, the target's access tokens stop working on the next request (401), without reinstalling the app. 204 on success, 403 out of scope, 404 `USER_NOT_FOUND`. Audit `auth.sessions_killed` (actor = administrator, entity = target user), written even when no session was active. Test: `KillSessions_RevokesTargetImmediatelyWithinScopeAndAudits`.

## Audit search (step 0.8)

`GET /audit-logs` — permission `audit.read`; scope tenant = all entries of the tenant, region = entries by actors in teams of the caller's region (plus the caller), own = the caller's entries. Filters: `action`, `actorId`, `entityId`, `from`, `to`; newest first; keyset pagination with `limit` (1–100, default 50) and `cursorOccurredAt` + `cursorId`. Returns only id, actor, action, entity and time. Invalid query → 400 `INVALID_DATA`. There is no update or delete route; the `audit_logs` trigger rejects UPDATE, DELETE and TRUNCATE even for the table owner. Test: `AuditApi_SearchesWithinScopeAndRowsCannotBeChanged`. `auth_login_audit` is not exposed yet.

## Security baseline (step 0.9)

- **Runtime grants:** `db/grants/lifedrop_app_grants.sql` is the single least-privilege grant list for `lifedrop_app`. `DeploymentRehearsalTests` applies the same file to a temporary NOSUPERUSER/NOBYPASSRLS role and calls every endpoint group through it; removing a needed grant fails the test.
- **Deployment gap found:** `scripts/deploy_customer_api/02_apply_changes.sql` (applied to `lifedrop_dev`) grants nothing on `visits`, `regions`, `teams` or `team_members`, so visit endpoints there would fail with `permission denied`. After migrations 0006–0009, apply `db/grants/lifedrop_app_grants.sql` on that database (owner action; not done from CI).
- **Protected routes:** `EveryRouteRequiresAuthorizationExceptReviewedPublicRoutes` fails if any route other than `GET /`, `POST /auth/login`, `POST /auth/refresh`, `POST /auth/devices/register` and the Development-only OpenAPI document lacks authorization.
- **No secrets in logs:** the rehearsal captures all API logs at Debug level and asserts that the password, access and refresh tokens (before and after rotation) and the device secret never appear.

## Visit planning (step 1.2)

Migration 0010 adds `visit_type` (`pharmacy` | `doctor` | `wholesaler` | `other`, default `other`; selects the report form later), `priority` (`high` | `normal` | `low`, default `normal`), optional `planned_time` (local time, Asia/Baghdad), `is_unplanned` and `unplanned_reason`. Several visits per customer per day are allowed.

| Route | Permission | Behaviour |
| --- | --- | --- |
| `POST /visits` | `visits.write` + scope | Optional `visitType`, `priority`, `plannedTime`; unknown values → 400 `INVALID_DATA` |
| `POST /visits/unplanned` | `visits.create_unplanned` + scope | For the caller only, dated today in Asia/Baghdad; `reason` 1–500 chars required; customer must be in the caller's scope (404 `CUSTOMER_NOT_FOUND`) |
| `GET /visits` | authenticated | Caller's own visits only. `scheduleDate` optional; default is today in Asia/Baghdad (step 1.3). Ordered by `plannedTime` (unscheduled last), then priority high → low, then creation. Empty day → 200 `[]`; malformed date → 400 `BAD_REQUEST` |

`visits.create_unplanned` is granted to the default SalesRep and ScientificRep roles (0010 replaces `seed_default_roles`; 0009 is unchanged). Test: `VisitPlanning_StoresDetailsAndEnforcesUnplannedRules`; domain tests in `VisitPlanningTests`.

Step 1.3 also sets `RouteHandlerOptions.ThrowOnBadRequest = false`, so malformed route, query or body values return 400 ProblemDetails in every environment instead of a 500 in Development.

## Visit reassignment and cancellation (step 1.4)

| Route | Permission | Behaviour | Audit |
| --- | --- | --- | --- |
| `PUT /visits/{id}/assignee` | `visits.write` + scope | Only `Planned` visits (else 400 `INVALID_STATE`); visit outside scope → 404 `NOT_FOUND`; new assignee outside scope → 403; unknown → 404 `USER_NOT_FOUND`; same assignee → 400 `INVALID_DATA`; changed meanwhile → 409 `CONCURRENCY_CONFLICT` | `visit.reassigned` |
| `POST /visits/{id}/cancel` | `visits.cancel` + scope | From any active state with a reason (1–500 chars); terminal afterwards | `visit.cancelled` |

Migration 0011 grants `visits.cancel` to the default SalesManager and Supervisor roles (seed function replaced; earlier migrations unchanged). Test: `ManageVisit_ReassignAndCancelWithinScopeAndAudit`.

## Customer location and geofence (step 1.5)

| Route | Permission | Behaviour | Audit |
| --- | --- | --- | --- |
| `PUT /customers/{id}/location` | `customers.location.write` + scope | `latitude`, `longitude`, `source` (`gps` needs `accuracyMeters` 0–100; `manual`); rejects out-of-range and 0,0; sets status `located`, source, time and actor | `customer.location_changed` |
| `PUT /customers/{id}/geofence` | `customers.geofence.write` + scope | `radiusMeters` 5–1000, or null for the default 20 m | `customer.geofence_changed` |

`GET /customers` and `GET /customers/{id}` now return `latitude`, `longitude`, `locationSource`, `locationUpdatedAt` and the effective `geofenceRadiusMeters`. Migration 0012 adds `location_accuracy_m`, `geofence_radius_m` and a source check, and grants `customers.location.write` to SalesManager, Supervisor, SalesRep and `customers.geofence.write` to SalesManager, Supervisor. The runtime grants file now includes `UPDATE` on `customers`. Test: `CustomerLocation_SetsLocationAndRadiusWithinScopeAndAudits`.

## Visit report (step 1.6)

| Route | Who | Behaviour | Audit |
| --- | --- | --- | --- |
| `PUT /visits/{id}/report` | Assigned user only | Only while `InVisit` (else 400 `INVALID_STATE`); `outcome` ∈ `products_presented`, `order_intent`, `follow_up_needed`, `no_interest`, `other`; `note` 3–2000 chars with letters or digits; `contractVersion` 1 (default). Re-saving while `InVisit` updates the report | `visit.report_saved` |
| `GET /visits/{id}/report` | Assignee, or `visits.write` scope covering the assignee | 200 report; 404 if absent or out of scope | — |

Migration 0013 adds `visit_reports` (one per visit, tenant RLS). There is no "order placed" outcome until orders exist (PRD §5.3). The database write is itself limited to an `InVisit` visit of the actor, so a bypass of the service check still cannot store a report. Check-out will require a saved report (step 1.8). Test: `VisitReport_SavedByAssigneeOnlyInVisitAndAudited`; domain tests `VisitReportTests`.

## Server-side check-in (step 1.7)

`POST /visits/{id}/start` (breaking change for clients: a body is now required)

- Body: `latitude`, `longitude`, `accuracyMeters`, `capturedAt` (device time, ISO 8601), `isMocked` (Android mock-location flag). Optional header `Idempotency-Key` (1–100 chars).
- Only the assignee; the access token must carry `device_id` of an approved device (403 `DEVICE_REQUIRED`); the visit must be `Arrived` (400 `INVALID_STATE`); the customer must have a location (409 `CUSTOMER_LOCATION_MISSING`).
- The server measures the distance with PostGIS and refuses (422) a position that is: mocked `GPS_MOCKED`, more than 30 s in the future `GPS_CLOCK_SKEW`, older than 2 min `GPS_STALE`, less accurate than 50 m `GPS_LOW_ACCURACY`, or farther than the customer's radius (default 20 m) `OUTSIDE_GEOFENCE`.
- Every attempt is stored in `visit_location_events` (migration 0014; accepted or with its rejection reason, device, distance, radius, accuracy and device time) and audited (`visit.checked_in` / `visit.check_in_rejected`). Coordinates are never written to audit or logs.
- A retry with the same `Idempotency-Key` after success returns 204 without a second record.

Test: `CheckIn_RequiresDeviceFreshGpsInsideGeofenceAndRecordsEveryAttempt`; domain `LocationCheckPolicyTests`; the deployment rehearsal checks in with a real device-bound login through the restricted role.

## Server-side check-out (step 1.8)

`POST /visits/{id}/complete` (breaking change for clients: same body and `Idempotency-Key` as `/start`)

- Only the assignee with an approved device in the token; the visit must be `InVisit` (400 `INVALID_STATE`) and must have a saved report (409 `REPORT_REQUIRED`).
- Same GPS rules as check-in (D2): mocked, clock skew, stale, low accuracy or outside the customer's radius is refused (422) and recorded in `visit_location_events` with its reason.
- The database update itself refuses `Completed` without a report row. After completion the report can no longer be edited.
- Audit: `visit.checked_out` / `visit.check_out_rejected`. A retry with the same `Idempotency-Key` returns 204 without a second record.

Test: `CheckOut_RequiresReportDeviceAndGpsInsideGeofence`; the deployment rehearsal checks out through the restricted role.

## Visit status history (step 1.9)

| Route | Who | Behaviour |
| --- | --- | --- |
| `GET /visits/{id}/history` | Assignee, or `visits.write` scope covering the assignee (same as the report) | 200 list, oldest first: `actorId`, `fromStatus` (null on creation), `toStatus`, `reason` (for `Cancelled`/`Failed`), `occurredAt`; 404 if absent or out of scope |

- Migration 0015 adds `visit_status_history` (tenant RLS, append-only). A trigger on `visits` writes a row on every insert and status change, so no code path can skip it. Writers name the actor with `app.current_actor`; a status update without it is refused by the database. A row inserted without an actor (data import) is kept with a null actor.
- New audit actions: `visit.created`, `visit.created_unplanned`, `visit.en_route`, `visit.arrived`, `visit.failed` (with `visit.reassigned`, `visit.cancelled`, `visit.report_saved`, `visit.checked_in`/`checked_out` and their rejections, every visit action is now audited). No coordinates are stored in history or audit.
- The runtime grants file adds `SELECT, INSERT` on `visit_status_history`.

Test: `VisitHistory_RecordsEveryTransitionWithActorAndIsReadableWithinScope`.

## Daily plan data for mobile (step 1.10)

`GET /visits` items now also carry `customerName` and `customerCode` (additive, nullable) so the mobile plan can show the customer without another request. Visibility is unchanged: only the caller's own visits for the day.

## Shifts and tracking (step 1.13a, decision D1)

All routes act on the caller's own shift only (another user's or tenant's shift reads as 404) and, except `GET /shifts/current`, need an access token bound to an approved device (403 `DEVICE_REQUIRED`).

| Route | Behaviour | Audit |
| --- | --- | --- |
| `POST /shifts` | Starts a shift on the token's device (201); if one is open, returns it (200). At most one open shift per user (unique index) | `shift.started` |
| `GET /shifts/current` | The open shift, or 404 | — |
| `POST /shifts/{id}/end` | Ends it (204); ending again also returns 204 | `shift.ended` |
| `POST /shifts/{id}/points` | 1–200 points `{clientPointId, latitude, longitude, accuracyMeters, capturedAt, isMocked}` from the shift's device (else 403 `DEVICE_MISMATCH`). Points that are malformed, before the start, after the end or more than 30 s in the future are counted as `rejected`; a `clientPointId` already stored (or repeated in the batch) counts as `duplicate`. Returns `{accepted, duplicates, rejected}` | — (points are the record) |
| `GET /shifts/{id}/summary` | `pointCount`, `expectedPoints` (one per 3 minutes), `coveragePercent`, `maxGapSeconds` (including start→first and last→end/now), `meetsTarget` (≥ 90 % and ≤ 5 min) — for the real-device test | — |

Migration 0016 adds `shifts` and `tracking_points` (tenant RLS). Coordinates are never written to audit or logs. Viewing other users' points is for the live map (step 1.19, D3). Retention (D6) is still open. Tests: `Shift_StartTrackEndWithDeviceDedupAndSummary`, `ShiftSummaryTests`, and the deployment rehearsal.

## Offline check-in / check-out and manager review (step 1.14a, decision D5)

`POST /visits/{id}/start` and `/complete` accept two more fields: `isOffline` (saved on the phone without internet) and `elapsedSinceCaptureMs` (time since capture by the phone's monotonic clock, unaffected by changing the phone's clock).

- Offline attempts: same rules as online (device, owner, state, report, accuracy ≤ 50 m, inside the radius, not mocked, not in the future) except freshness: up to **24 h** old (else 422 `OFFLINE_EXPIRED`). Online attempts keep the 2-minute rule.
- An accepted offline attempt answers **200** `{reviewRequired, reviewReasons}` (online and replays stay 204). It needs review when the monotonic time and the GPS time differ by more than 2 min (`clock_mismatch`, also when not sent), or when the user's tracking path has no point within radius + accuracy + 50 m of the customer within ±10 min (`no_tracking_nearby`). Audit `visit.offline_review_required`.

| Route | Who | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /offline-reviews?state=pending\|approved\|rejected&limit=1..200` | `visits.write` with tenant or region scope | Events of users in scope: visit, user, kind, capture/receive time, distance, radius, accuracy, reasons, decision. No coordinates | — |
| `POST /offline-reviews/{eventId}` | same; never one's own event | `{decision: approve\|reject, note}`; reject needs a note (≤ 500). 404 outside scope, 409 `INVALID_STATE` when already decided | `visit.offline_review_approved` / `_rejected` |

The decision does not change the visit's status; it is recorded for reports and follow-up. Migration 0017 adds the offline and review columns to `visit_location_events` (grant `UPDATE` added). Tests: `OfflineCheckpoints_AutoAcceptReviewOrRejectAndManagerDecides`, `OfflineCheckPolicyTests`.

## Customer registration and approval (step 1.15, decision D11)

- `POST /customers` takes an optional `regionId` (default: the creator's team region; unknown region → 404 `REGION_NOT_FOUND`). A creator without tenant-wide `customers.write` scope who registers a customer in another region creates it as `pending` (audit `customer.created_pending`); otherwise `approved` (`customer.created`). The customer stays owned by the creator. Customer reads now include `regionId` and `approvalStatus`.
- Visits (`POST /visits`, `/visits/unplanned`) for a `pending` or `rejected` customer → 409 `CUSTOMER_NOT_APPROVED`.

| Route | Who | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /customer-approvals` | `customers.approve` (SalesManager: tenant; Supervisor: own region) | Pending customers in scope | — |
| `POST /customers/{id}/approval` | same | `{decision: approve\|reject, note}`; reject needs a note; outside scope 404; decided 409 `INVALID_STATE` | `customer.approved` / `customer.rejected` |

Migration 0018 adds the approval columns and the `customers.approve` permission (default roles updated). Test: `CustomerApproval_OutOfRegionCustomerWaitsForManagerOrRegionSupervisor`.

## Panel read routes: current user, users, roles, devices (step 1.17)

Read-only routes for the web panel. The panel hides buttons by `/me` permissions for convenience only; every change goes through its own route (`/devices/{id}/approve|block`, `/users/{id}/sessions/revoke`, `/users/{id}/roles/{roleId}`, `/customers/{id}/approval`), which checks permission, scope and tenant again.

| Route | Who | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /me` | any signed-in active user | `{id, email, roles, permissions}` of the caller | — |
| `GET /users` | `sessions.kill`, `user.manageroles`, `system.admin` or (step 1.18) `visits.write`; widest scope of these (tenant: all; region: users in teams of the actor's region + self; own: self) | Active users of the tenant (max 500; with `includeInactive=true` and `user.manageroles`/`system.admin` also deactivated ones): email, roles, team, region, number of active session families, `isActive` | — |
| `GET /roles` | `user.manageroles` or `system.admin` | Roles of the tenant: id, name, data scope | — |
| `GET /devices?status=pending\|approved\|blocked` | `device.approve`, `devices.manage` or `system.admin` (same as approve/block) | Devices of the tenant (max 500, newest first) with owner email; never the secret. Unknown status → 400 `INVALID_DATA` | — |
| `GET /devices?platform=android\|ios&q=text` | same | Extra filters (step 4.39): phone type, and text in the phone's name, the manager's label, the model or the user's e-mail (`%`/`_` literal, ≤100); each row also has `adminLabel`, `model`, `osVersion`; deleted devices are never listed | — |
| `PUT /devices/{id}/label` | `device.approve`, `devices.manage` or `system.admin` | Body `{label}` (empty/null clears, ≤80); shown on the live map instead of the phone's own name; 204, 400, 403, 404 (other company or deleted) | `device.label_changed` |
| `DELETE /devices/{id}` | same | Blocks the device, ends its sessions, hides it for good and frees its install id so the phone can register again; history stays; 204, 403, 404 | `device.deleted` |
| `POST /auth/devices/register` | (step 4.39) the body may also carry `model` and `osVersion` (optional, cut to 100/40 characters) | — | — |

Test: `PanelDirectory_ListsUsersDevicesAndRolesWithinScope` (scope, foreign tenant, status filter, no secret). Panel tests: `src/LifeDrop.Web/src/pages/Panel.test.tsx`.

## Panel visit planning, regions and teams (step 1.18)

| Route | Permission and scope | Response | Audit |
| --- | --- | --- | --- |
| `GET /visit-plan` | `visits.write` (same as create/reassign); tenant scope: every visit of the day; region: visits of the actor and of members of teams in the actor's region; own: the actor's visits | `scheduleDate` optional (default today, Asia/Baghdad), `assignedUserId` optional filter; 200 list of visits (same shape as `GET /visits`, plus `assigneeEmail`), same order; 403 without `visits.write`; 400 malformed query | — |

`GET /users` also accepts `visits.write`, so a planner can choose assignees within the same scope. The panel's Visits tab uses `POST /visits`, `PUT /visits/{id}/assignee` and `POST /visits/{id}/cancel`; the Regions & teams tab uses the routes of step 0.6a (`/regions`, `/teams`, `/teams/{id}/region`, `/teams/{id}/members/{userId}`). Each keeps its own server checks; the customer approval queue stays on `/customer-approvals` (step 1.17 screen). Tests: `VisitPlan_ListsOneDayWithinVisitsWriteScope`; panel `Panel.test.tsx`.

## Live map (step 1.19, decision D3)

| Route | Permission and scope | Response | Audit |
| --- | --- | --- | --- |
| `GET /live-map` | `tracking.read` (exact positions) and/or `tracking.summary` (status only); rows come from the wider of the two scopes (tenant: all; region: the actor and members of teams in the actor's region; own: the actor); coordinates only for users the `tracking.read` scope covers | 200 list of active users with an open shift: `email`, `teamName`, `shiftStartedAt`, `lastUpdateAt`, `isLive` (last point ≤ 5 min old), `precise`, `latitude`/`longitude`/`accuracyMeters`/`isMocked` (null without `tracking.read`), `lastCustomerName`/`lastVisitAt` (last check-in since the shift started); 403 without either permission | — |

Default grants (migration 0019, also for existing tenants): SalesManager `tracking.read` (tenant), Supervisor `tracking.read` (region), FinanceManager `tracking.summary`. Tests: `LiveMap_ShowsPositionsOrStatusByPermissionWithinScope`, `LiveMapTests` (application), panel `LiveMap.test.tsx`.

## Audit viewer (step 1.20)

`GET /audit-logs` rows also carry `actorEmail` (additive, nullable). The rules are unchanged: `audit.read`, the same scope, and no route that changes or deletes rows. The panel's Audit tab filters by action, actor, entity and Iraq-local days (`from` = 00:00 and `to` = 23:59:59.999 at +03:00), and pages with the cursor of the last row. It offers no edit or delete. Tests: `AuditApi_SearchesWithinScopeAndRowsCannotBeChanged`, panel `Audit.test.tsx`.

## Flexible visit planning, part 1 (step 1.23)

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `PUT /visit-plan/order` | The plan's owner needs `visits.plan` (SalesRep, ScientificRep); anyone else needs `visits.write` with the assignee inside their data scope (out of scope: 404; neither permission: 403) | Body `scheduleDate`, `visitIds` (the whole day of one assignee, 1-500, distinct) and optional `assignedUserId` (default: the caller). Positions 1..n in one transaction, only if every visit belongs to that assignee on that day (otherwise 409 `CONCURRENCY_CONFLICT`, nothing changes). Any visit status may be ordered | `visit.reordered` (entity = assignee) |
| `POST /visits/{id}/postpone` | Same rule, judged on the visit's assignee | Body `scheduleDate` (today in Asia/Baghdad or later, not the current day) and optional `reason` (max 500). Only `Planned` or `EnRoute` visits (400 `INVALID_STATE` otherwise); status goes back to `Planned`, the manual position is cleared, `postpone_count` grows. 204 | `visit.postponed` (entity = visit); an en-route visit also gets a status-history row |
| `GET /customers/{id}/visit-windows` | `customers.read`, customer inside the actor's data scope (else 404) | Days (0 = Sunday .. 6 = Saturday) and local hours in which the customer accepts visits; empty = no restriction | — |
| `PUT /customers/{id}/visit-windows` | `customers.write`, customer inside the actor's data scope | Replaces the whole list: at most 21 rows, weekday 0-6, opens before closes, no overlap inside one day (else 400 `INVALID_DATA`) | `customer.visit_windows_changed` |

`GET /visits` and `GET /visit-plan` now order a day by the manual position first (unordered visits last), then planned time, priority and creation, and every visit carries `sortOrder`, `postponeCount`, `postponeReason` and `windowStatus` (`none` = the customer has no windows, `within`, `outside`; a visit without planned time only needs the day to be accepted). Migration 0020 adds `visits.sort_order`, `postpone_count`, `postpone_reason`, the table `customer_visit_windows` (RLS) and the permission `visits.plan` (SalesRep, ScientificRep; also backfilled); the grants file covers the new table. Tests: `FlexiblePlan_OwnerAndManagersReorderAndPostponeWithinScopeAndAudit`, `CustomerVisitWindows_AreEditedWithinScopeAndFlagVisitsOutsideThem`, `VisitPostponeTests`, `CustomerVisitWindowsTests`, mobile `PlanChangeTests`, panel `Panel.test.tsx`.

## Places tree and customer profile (step 1.23-b, part 1: server and panel)

One tree per company: city -> neighbourhood -> street -> alley (an alley sits under a street or straight under a neighbourhood). Any employee adds a neighbourhood, street or alley and it is part of the reference at once (no approval); managers fix, move, deactivate and merge. The tree rules are also enforced by a database trigger.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /places?parentId=&kind=&q=&includeInactive=&limit=` | `customers.read` (`includeInactive` only counts with `places.manage`) | `kind=city` lists the cities, `parentId` what is directly below a place, `q` searches all four names (case-insensitive, literal `%`/`_`), limit 1-5000 (default 500). Active places only unless `includeInactive`; each row: `id`, `parentId`, `kind`, `name`, `nameCkb`, `nameAr`, `nameEn`, `status`, `mergedInto`, `childCount` | — |
| `POST /places` | A city needs `places.manage`; a neighbourhood, street or alley needs `customers.write` | Body `parentId` (null for a city), `kind`, `name` (1-160), optional `nameCkb`/`nameAr`/`nameEn`. Created `active` at once. 404 unknown parent, 400 inactive/merged parent or a parent of the wrong kind, 409 `DUPLICATE_NAME` (same name under the same parent, case/space-insensitive) | `place.created` | Since step 4.45 a name that only differs in the letters of another keyboard (Arabic ي ك ى ھ, Persian/Kurdish ی ک ه), in digits (٠-٩ ۰-۹ → 0-9), in diacritics/tatweel/zero-width characters or in the number of spaces is the same place: 409 `DUPLICATE_NAME` (also on rename/move); the import reuses the place that exists. The Kurdish ە is a different letter.
| `PUT /places/{id}` | `places.manage` | Rename, set `status` (`active`/`inactive`) and, with `parentId`, move the place (same kind rules; its customers and places below go along). A merged place is 404; a name already used under the target is 409 | `place.updated` |
| `POST /places/{id}/merge` | `places.manage` | Body `intoPlaceId`: same kind, target active and not merged (409 `CONCURRENCY_CONFLICT` otherwise), not itself (400). Customers and places below move to the target, the place becomes inactive with `mergedInto`; a name clash below the target stops the merge (409, nothing changes) | `place.merged` |
| `POST /places/import` | `places.manage` | Body `rows` (1-2000): `kind`, `name`, `city`, `neighbourhood`, `street`, `nameCkb`/`nameAr`/`nameEn`, `active`. Needs the levels above (city: none, its `city` column is ignored; neighbourhood: city; street: city + neighbourhood; alley: city + neighbourhood, street optional); missing places above are created; existing ones are skipped, never overwritten; one invalid row rejects the request. Answer `created`, `skipped` | `places.imported` |
| `GET /places/export?format=json\|csv&includeInactive=` | `places.manage` | The whole tree for other projects: `id`, `parentId`, `kind`, `city`, `neighbourhood`, `street`, `name`, three languages, `status`. CSV (UTF-8 with BOM) has the same columns as the import file, so it can be imported again; a text starting with `=`, `+`, `-` or `@` is written with a leading apostrophe so a spreadsheet keeps it as text | — |
| `PUT /customers/{id}/profile` | `customers.write`, customer inside the data scope (else 404) | Replaces `phone` (max 40), `address` (max 300) and `placeId` (the most precise place known: any kind; null clears). The place must be active and not merged (else 400 `INVALID_DATA`) | `customer.profile_changed` |

`GET /customers` and `GET /customers/{id}` also return `phone`, `address`, `placeId` and `placePath` (the place and the ones above it, from the city down: `id`, `kind`, `name`). Migration 0021 adds the table `places` (RLS, one live name per kind under one parent, tree trigger), `customers.place_id` and the permission `places.manage` (SalesManager, Supervisor; also backfilled); the grants file covers the new table. Tests: `Places_TreeImportAddAndEditByPermissionAndCustomerProfile`, `Places_MergeExportAndImportAgainWithinTenantAndTreeRulesInTheDatabase`, `PlaceExportTests`; panel tests in `Panel.test.tsx` and `csv.test.ts`. Starter list: `docs/places-starter.csv`.

## Region reps and notifications (step 1.23-c)

The default `SalesRep` role now has data scope `region` (migration 0022, also for existing tenants that kept the default): `customers.read`, `customers.write` and `customers.location.write` cover the customers of the region of the rep's team (a customer is in the region when its `region_id` is that region) and the ones the rep added; a rep without a team keeps to the customers they added. `ScientificRep` is unchanged (`own`).

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /notifications?unread=&limit=` | Signed-in user; only their own messages | Newest first, limit 1-200 (default 50). Each: `id`, `kind` (`customer.profile_changed` / `customer.location_changed`), `entityId` (customer id), `actorId`, `actorEmail`, `customerCode`, `customerName`, `createdAt`, `readAt` | — |
| `GET /notifications/unread-count` | same | `{count}` | — |
| `POST /notifications/{id}/read` | same | Marks one of the user's own messages read (again: harmless); somebody else's or unknown: 404 | — |
| `POST /notifications/read-all` | same | Marks all of the user's messages read | — |

Who is told: when somebody who is **not the owner** of a customer changes its profile (`PUT /customers/{id}/profile`) or location (`PUT /customers/{id}/location`), the message goes to the supervisors of the customer's region (users whose role has `customers.approve` with region scope and who are in a team of that region); when the region has none, to the sales managers (`customers.approve` with tenant scope). The person who made the change is never told, a supervisor's own change tells only the other supervisors, and the owner's own changes tell nobody. The message is stored in the same transaction as the change. Migration 0022 adds `notifications` (RLS); the grants file covers it. Test: `RegionReps_EditRegionCustomersAndTheResponsibleAreNotified`; panel: the Notifications tab with the unread count (`Panel.test.tsx`).

## Custom visit-form fields (step 3.2, part 2)

New permission `forms.manage` (SalesManager, CompanyManager; migration 0040 also grants it to existing companies). Definitions are per company; answers belong to a visit.

| Route | Permission and scope | Behaviour |
| --- | --- | --- |
| `GET /form-fields?visitType=&includeInactive=` | Signed-in user | Active fields for that visit type (`pharmacy`/`doctor`/`wholesaler`/`other`; fields for all types always); `includeInactive` only counts for `forms.manage`; bad type 400 |
| `POST /form-fields` | `forms.manage` | `{key, type, appliesTo, labelEn/Ckb/Ar/Fa (at least one, ≤100), options (choice: 2-20 distinct), isRequired, sortOrder 0-1000}`; key `^[a-z][a-z0-9_]{1,39}$`, unique per company (409); type `text`/`number`/`choice`/`yes_no`/`date`; appliesTo `all` or one visit type; at most 30 active fields (409). Audit `form_field.created` |
| `PUT /form-fields/{id}` | `forms.manage` | Labels (not given = keep, blank = remove, one must stay), options (choice only), isRequired, sortOrder, isActive. Key, type and visit type never change; fields are never deleted. Audit `form_field.updated` / `form_field.deactivated`; unknown 404 |
| `PUT /visits/{id}/report/custom` `{answers: [{fieldId, value}]}` | The assigned user, visit `InVisit` (else 403 / 409; unknown visit 404) | Replaces the answer set. Only active fields for the visit's type; every required field answered; values are strings checked by type (number ≤3 decimals, choice from options, yes_no `true`/`false`, date `yyyy-MM-dd`, text ≤1000); a blank value removes an answer; duplicates 400. Audit `visit.custom_answers_saved` |
| `GET /visits/{id}/report/custom` | Same readers as the visit report | Answers with the field key; 404 when nothing saved or not readable |

## Saved and scheduled reports (step 3.2, part 1)

Permission `reports.read` for every route; everybody reads, runs, changes and deletes **only their own** reports (another user or company gets 404). A run always uses the owner's current area (docs/KPI.md rules).

| Route | Behaviour |
| --- | --- |
| `GET /saved-reports` | The caller's reports with their schedule |
| `POST /saved-reports` | `{name, range, from?, to?, regionId?, teamId?, customerType?, schedule?}`; `range` = `yesterday` / `last_7_days` / `last_30_days` / `this_month` / `last_month` (Iraq days, worked out when it runs) or `fixed` (from + to, at most 366 days); schedule `{frequency: daily/weekly/monthly, weekday 1-7 (weekly), dayOfMonth 1-28 (monthly), hour 0-23 Iraq time, isActive}`; 400 on anything else; 409 for a name the owner already has or more than 20 reports |
| `PUT /saved-reports/{id}` | Replaces the definition (and schedule; none = unscheduled); 404 if not the caller's |
| `DELETE /saved-reports/{id}` | Deletes it and its runs |
| `POST /saved-reports/{id}/run` | Runs now (`manual`), keeps the result, returns it; the schedule does not move |
| `GET /saved-reports/{id}/runs?limit=` | Newest first (1-30, default 30; the newest 30 are kept) |
| `GET /saved-reports/{id}/runs/{runId}` | One run with the whole stored KPI result |

Scheduled runs: a background job (`Reports:ScheduleIntervalSeconds`, default 60, 0 = off) runs each due report once as its owner and moves `next_run_at`. An owner who lost `reports.read` gets a stored `failed` run (`FORBIDDEN`), never silence. Paused schedules never run.

## Platform: companies and cross-company access (step 3.1)

Callers must belong to the platform company (id `00000000-0000-0000-0000-00000000f001`, code `platform`) **and** hold the permission; a role carrying the same permission key in an ordinary company gives nothing (403). The `SuperAdmin` role (platform company only, migration 0038) has both permissions; no user is seeded. Every action writes `platform_audit_logs` (append-only); a cross-company read writes its row in the same transaction as the read.

| Route | Permission | Behaviour |
| --- | --- | --- |
| `GET /platform/tenants` | `platform.tenants.manage` | All companies with code, name, active flag, user counts |
| `POST /platform/tenants` `{code, name, managerEmail, managerPassword}` | same | Code `^[a-z0-9][a-z0-9-]{1,31}$` (not `platform`), name ≤100, password ≥12; creates the company, its default roles and a first CompanyManager (password stored hashed); 409 when the code or email is taken. Audit `platform.tenant_created` |
| `PUT /platform/tenants/{id}` `{name?, isActive?, reason}` | same | Rename and/or activate/deactivate (a deactivated company cannot log in and its sessions stop); reason 5-500 chars required; the platform company cannot be deactivated (409); unknown 404. Audit `platform.tenant_renamed/_activated/_deactivated` |
| `PUT /platform/tenants/{id}/profile` | Super Admin of the platform company (`platform.tenants.manage`) | Body `{industry pharma\|food\|null, hiddenModules[] from catalog, orders, payments, samples, followups, warehouse, textOverrides {"app.…"\|"web.…": {ckb\|ar\|fa\|en: text ≤300}} up to 200 texts, reason 5-500}`; replaces the whole profile; 204, 400, 403, 404 | platform audit `platform.tenant_profile_changed` (industry, hidden modules, number of texts) |
| `GET /company-config/{code}` (step 4.48) | Public (reviewed) | Also returns `industry` of the company (its own setting, else the server default), `hiddenModules` and `texts` (the company's own wording). Public facts only | — |
| `GET /platform/tenants/{id}/audit-logs?reason=&action=&limit=` | `platform.tenants.access` | Read-only view of that company's audit log, newest first (limit 1-200, default 50); `reason` 10-500 chars required (400 otherwise); unknown company 404 and nothing is written. Audit `platform.tenant_audit_read` with the reason |
| `GET /platform/audit-logs?tenantId=&limit=` | `platform.tenants.manage` | The platform audit, newest first |

## Push tokens (step 2.14)

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `PUT /push-token` `{token, platform}` | Signed-in user, for themselves | Token 20-4096 chars, platform `android`/`ios`, else 400. Repeating is harmless; a token already held by another user of the same company moves to the caller | — |
| `POST /push-token/remove` `{token}` | same | Removes the caller's own token (sign-out); somebody else's or unknown: 404 | — |

## Plan from places (step 1.24)

No migration. Both routes need `visits.write` (SalesManager, Supervisor) and apply the caller's data scope twice: the assignee must be inside the scope (as for `POST /visits`), and only customers inside the scope are candidates.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `POST /visit-plan/preview` | `visits.write`; assignee and customers in scope | Body: `assignedUserId`, `scheduleDate` (today or later, Iraq time), `placeId` and/or `regionId` (at least one), optional `count` (1-200; empty = all), `order` (`least_recent` default / `name` / `code`), `respectWindows` (default true), `visitType`, `priority`. A `placeId` includes every place below it. Candidates: approved customers that have no visit (other than Cancelled/Failed) on that day, max 2000. Result `{matching, included, candidates[]}`; each candidate has `code`, `name`, `type`, `place` path, `lastVisitDate` (last Completed visit), `windowStatus`, `included`, `skipReason` (`outside_hours` = the customer's weekly hours do not include that weekday; `over_limit`). Creates nothing. 400 `INVALID_DATA`, 403, 404 `USER_NOT_FOUND` | — |
| `POST /visit-plan/generate` | same | Same body plus `customerIds` (the rows kept in the preview; `count` is then ignored). The server recomputes the candidates and creates visits only for those that are still included; `{created, skipped, visitIds}`. Visit type = by customer type unless given; priority default normal. New visits go after the ones already in the assignee's day (their current order is fixed first). One transaction | `visit.created` per visit and one `visit.plan_generated` (entity = assignee) |

Test: `PlanFromPlaces_PreviewThenCreateByPlaceRegionCountOrderAndHours`; panel: the "Plan from places" form on the Visits tab (`Panel.test.tsx`).

### Planning for oneself (step 1.25)

The same two routes also serve a field user **without** `visits.write` but with `visits.plan` (SalesRep, ScientificRep), for their own plan only: `assignedUserId` is left out (= the caller) or is the caller; any other assignee is 403. The candidates are limited to the customers the caller may read (`customers.read` scope: region customers for a rep in a team, otherwise the ones they added). New optional field `query`: only customers whose code or name contains the text (max 60 characters, `%` and `_` are plain characters); a request needs `placeId`, `regionId` or `query`. Test: the last part of `PlanFromPlaces_PreviewThenCreateByPlaceRegionCountOrderAndHours`; app: `SelfPlannerTests`, page `AddToPlanPage` (button on the plan page).

## Customer priority from a manager (step 1.26)

Migration 0023: `customers.priority` (`high` / `normal` / `low`, default `normal`), `priority_set_by`, `priority_set_at`. No new permission: it needs `visits.write` (SalesManager, Supervisor) and only customers inside that data scope change.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `PUT /customers/priority` | `visits.write`; customers in scope | Body: `priority` and exactly one of `customerIds` (1-200) or `placeId` (every customer in that place and below it). Only customers whose priority differs change. 200 `{updated}`; 400 `INVALID_DATA` (both or none of the two, unknown priority, over 200 ids); 403 | `customer.priority_set` per changed customer |

Where it shows: `priority` on `GET /customers`, `customerPriority` on every visit of `GET /visits` and `/visit-plan` (the customer's priority; the visit's own `priority` is unchanged) and on every candidate of `/visit-plan/preview`. Planning from places and the rep's own add-customers list put `high` first, then `normal`, then `low`, before the chosen order (also when a count limits the list). Visits that already exist keep their own priority. Test: `CustomerPriority_ManagerSetsByIdsOrPlaceWithinScopeAndPlansShowAndUseIt`; panel: Customers tab priority bar; app: `PriorityNoteTests`.

## Plan from a drawn area (step 1.27)

No migration. `POST /visit-plan/preview` and `/generate` (both for `visits.write` users and, for themselves, `visits.plan` users) take a new filter `area`: the corners `[{latitude, longitude}]` of a shape drawn on the map: 3-100 points, real coordinates, a real surface, a box of at most 0.5 degrees (about 50 km) in each direction, else 400 `INVALID_DATA`. The server closes the shape (any start corner, either direction) and returns only customers with a position inside or on its border, approved, in the caller's scope and not yet planned that day. `area` counts as a filter like `placeId`, `regionId` and `query` and can be combined with them. Customers without a position are never found this way. Test: `PlanFromArea_DrawnAreaFindsLocatedApprovedCustomersInScopeAndRefusesBadShapes`; app: `AreaDrawingTests`, pages `DrawAreaPage` (Leaflet + OpenStreetMap in a WebView, online only) and the button on the add-customers page.

## Products, city prices and customer discounts (step 2.1)

Migration 0024: `products`, permissions `catalog.read` (SalesManager, Supervisor, FinanceManager, CompanyManager, SalesRep, ScientificRep) and `catalog.manage` (SalesManager); default roles and existing companies get them. Migration 0025 (owner decision 2026-10-07; replaces the region/customer price rules of 0024, which were never released): `city_prices`, `customer_discounts`. Both tables have RLS and grants. One currency per product (`IQD` or `USD`, no conversion), one unit per product. Money is `numeric(18,3)`.

**Which price a customer pays on a day:** the general price (`products.base_price`), or the price of the customer's **city** when that city has one for that day (the city is the place of kind `city` found above the customer's place; a customer without a place pays the general price); then every valid discount of the customer, oldest first, each on the price left (a percentage takes that share, an amount takes that much per unit), never below 0, rounded to three decimals. A discount on all products must be a percentage; an amount belongs to one product.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /products?includeInactive=&q=` | `catalog.read` | Active products by code; `includeInactive` only counts for `catalog.manage`; `q` matches code and names (`%` and `_` are plain characters); max 1000 | — |
| `POST /products` | `catalog.manage` | Body: `code` (1-40, letters, digits, `.` `_` `-`), `name`, optional `nameCkb/Ar/En`, `unit`, `currency`, `basePrice`, `status`. 201 `{id}`; 400; 409 `CONFLICT` for a repeated code | `product.created` |
| `PUT /products/{id}` | `catalog.manage` | Same fields (the code never changes); 204; 404 | `product.updated` |
| `PUT /products/{id}/details` | `catalog.manage` | Body `{specialty, manufacturer, expiryDate, stockQty, notes}` (all optional; null/empty clears; specialty from the fixed list; manufacturer ≤100; notes ≤500; stock ≥0 with ≤3 decimals, null = not tracked); 204, 400, 403, 404. `GET /products` now also returns these fields and `inStock` (not tracked or stock above 0). | `product.details_changed` |
| `PUT /products/{id}/photo` | `catalog.manage` | The request body is the picture itself (JPEG, PNG or WEBP, checked by its first bytes, up to 1 MB); replaces the old one with a new random key; 204, 400 not an image / too big, 403, 404 | `product.photo_changed` |
| `DELETE /products/{id}/photo` | `catalog.manage` | 204; 404 when there is no photo; 403 | `product.photo_removed` |
| `GET /product-photos/{key}` | **Public (reviewed)**: no login; the key is 32 random hex characters from the product's `photoKey`; the picture is cached for good because every upload makes a new key | The image; 404 for an unknown or old key | — |
| `GET /products/{id}/city-prices` | `catalog.read` | Newest start first, with `cityName`; 404 | — |
| `POST /products/{id}/city-prices` | `catalog.manage` | `cityId` (a place of kind city), `price`, `validFrom` (default today, Iraq), `validTo` (empty = open; both days count). 409 when the days overlap a price of the same product and city (checked under a lock on the product); 404 unknown product or city; 400 (also when the place is not a city) | `city_price.created` |
| `POST /city-prices/{id}/end` | `catalog.manage` | `validTo`: not before the start, not later than an end it has; prices are never deleted | `city_price.ended` |
| `GET /customers/{id}/discounts` | `catalog.read` and the customer inside the caller's `customers.read` scope (else 404) | All discounts of the customer, newest start first | — |
| `POST /customers/{id}/discounts` | `catalog.manage` | `productId` (empty = all products), `kind` (`percent` 0-100 or `amount` per unit; amount needs a product), `value` (> 0, three decimals), `validFrom`, `validTo`. Several can be valid together. 404 unknown customer or product; 400 | `discount.created` |
| `POST /discounts/{id}/end` | `catalog.manage` | Same end rule as city prices | `discount.ended` |
| `GET /customers/{id}/prices?date=` | `catalog.read` and the customer inside the caller's `customers.read` scope (else 404) | Every active product: `listPrice`, `listSource` (`base` or `city`), `cityPriceId`, the `discounts` applied in order and the final `price`. Later steps (orders) take prices from here, never from the phone | — |

Test: `Catalog_ManagerMaintainsProductsCityPricesAndDiscountsAndEveryoneGetsTheServerPrice`; unit: `PriceCalculatorTests`; panel: the Products tab (`Panel.test.tsx`).

## Warehouses and orders (step 2.2)

Migration 0026: `warehouses`, `order_counters`, `orders`, `order_items` (RLS, grants file updated), permissions `orders.create` (SalesManager, Supervisor, SalesRep), `orders.read` (those and FinanceManager, CompanyManager), `orders.cancel` (SalesManager, Supervisor), `warehouses.manage` (SalesManager); default roles and existing companies get them. Owner decisions 2026-10-07: orders are placed from the rep's app **and** the panel; **the person who places the order chooses the warehouse**; discounts apply per product or on all products as a percentage (the "on the total" case), see step 2.1. All lines of an order have one currency. Migration 0026 also lets `visit_reports.outcome` be `order_placed`.

Prices: the request carries **no price**. The server takes the general or city price of today (Iraq) and the customer's valid discounts (`PriceCalculator`), writes them on each line (`listPrice`, `unitPrice`, `lineTotal`, the applied `discounts`) and the order `total`, so a later price change never rewrites an order.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /warehouses?includeInactive=` | `orders.read` or `orders.create` | Active warehouses by code; inactive ones only for `warehouses.manage` | — |
| `POST /warehouses` | `warehouses.manage` | `code` (1-40), `name`, `status`. 201 `{id}`; 400; 409 repeated code | `warehouse.created` |
| `PUT /warehouses/{id}` | `warehouses.manage` | `name`, `status` (the code never changes); 204; 404 | `warehouse.updated` |
| `POST /orders` | `orders.create`; the customer must be inside the caller's data scope and approved | Body: `customerId`, `warehouseId` (active), optional `visitId` (a visit of that customer, of the caller or of someone in a `visits.write` scope, not cancelled or failed), `clientRef` (a new guid per order; a retry with the same one returns the same order with 200 and `replayed: true`, another person or customer with it: 409), `items` (1-100 lines, one per product, `quantity` > 0, at most three decimals, at most 100000; inactive or unknown products 400), optional `note` (500). 201 `{id, number, currency, total, replayed}`; `number` is `O-000001`... without gaps; 400 `INVALID_DATA` / `CURRENCY_MISMATCH`; 404 customer, warehouse or visit; 409 `CUSTOMER_NOT_APPROVED` | `order.created` |
| `GET /orders?customerId=&status=&limit=&before=&beforeId=` | `orders.read`; tenant: all; region: orders of the region's customers, of the user's customers and the ones the user placed; own: the user's own | Newest first, `limit` 1-100 (default 50), `before` + `beforeId` continue after the last row; `status` is `placed` or `cancelled` | — |
| `GET /orders/{id}` | same scope (else 404) | The order with its lines | — |
| `POST /orders/{id}/cancel` | `orders.read` scope; `orders.cancel` holders cancel any order in scope, the creator cancels their own | `reason` (1-500). 204; 409 when the order is not `placed` any more; 403 for someone who is neither; 404 out of scope | `order.cancelled` |
| (rule) order stock (step 4.41) | `POST /orders` | A product with a stock figure (`stockQty` not null) is locked and checked: when an order asks for more than is there, nothing is taken and the answer is 409 `OUT_OF_STOCK` (also for stock 0; the whole order is refused); otherwise the quantity is taken. A product without a stock figure is not limited. A retry of the same send takes nothing more. Cancelling (placed, preparing or ready) gives the quantity back once. A failed delivery does not change the stock: the manager corrects it in the product. | `order.created` / `order.cancelled` (stock moves with them) |
| `PUT /places/{id}/hide-prices` | `places.manage` | Body `{hidePrices}`; only a CITY can be ticked (else 404); 204, 400, 403 | `place.prices_visibility_changed` |
| `PUT /regions/{id}/hide-prices` | `regions.manage` | Body `{hidePrices}`; 204, 400, 403, 404 | `region.prices_visibility_changed` |
| (rule) hidden prices (step 4.42) | users with the restriction `catalog.prices.restricted` (SalesRep, ScientificRep); everybody else is unaffected | `GET /products` (own region ticked), `GET /customers/{id}/prices` and the orders (`POST /orders` result, `GET /orders`, `GET /orders/{id}`) show every amount as 0 with `pricesHidden = true` when the customer's city is ticked or the user's own region is ticked; quantities, stock and order creation work as usual | — |

The visit report accepts the outcome `order_placed` only when the visit has a `placed` order (`409 ORDER_REQUIRED` otherwise).

Tests: `Orders_ServerPricesWarehouseChosenByVisitorScopeCancelAndRetry` (also runs the phone's `OrdersApiClient` against the API), `OrdersTests` (app), panel tests in `Panel.test.tsx`.

## Warehouse work (step 2.3)

Migration 0027: `warehouse_staff`, `order_status_history`; orders get the statuses `preparing`, `ready`, `handed_over` and a `driver_id`; order lines get `prepared_quantity` and `shortage_reason`; permission `warehouse.work` (WarehouseManager role; also for existing companies). Owner decisions 2026-10-07: steps **placed → preparing → ready → handed over to a driver**; the worker may report a smaller prepared quantity per line **with a reason** (the order total does not change; finance decides later); the work is done **in the panel and in the app**, so the `WarehouseManager` role no longer requires an approved phone (the `Driver` role still does).

A warehouse worker sees and handles **only the orders of the warehouses they are assigned to**; an order of another warehouse is "not found" (404), a user without `warehouse.work` gets 403.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /warehouses/{id}/staff` | `warehouses.manage` | Users who work in that warehouse | — |
| `POST /warehouses/{id}/staff` | `warehouses.manage` | `userId`; only a user whose roles give `warehouse.work` (else 400); again for the same user: 204, nothing changes; 404 unknown warehouse or user | `warehouse.staff_added` |
| `DELETE /warehouses/{id}/staff/{userId}` | `warehouses.manage` | 204, or 404 when the user is not there | `warehouse.staff_removed` |
| `GET /warehouse/orders?status=&limit=&before=&beforeId=` | `warehouse.work`; own warehouses | Newest first; without `status` the open ones (`placed`, `preparing`, `ready`) | — |
| `GET /warehouse/orders/{id}` | same | The order with lines (ordered and prepared quantity, shortage reason), driver and the steps (`history`) | — |
| `POST /warehouse/orders/{id}/start` | same | `placed` → `preparing`; 409 in any other status | `order.preparing` |
| `POST /warehouse/orders/{id}/ready` | same | Body `lines`: every line once with `lineNo`, `quantity` (0 up to the ordered quantity, three decimals) and a `reason` when it is less. `preparing` → `ready`; 400 for missing, repeated or unknown lines, more than ordered, or no reason; 409 in another status | `order.ready` (+ `order.shortage` when something is short) |
| `POST /warehouse/orders/{id}/handover` | same | `driverId` of a user with the `Driver` role (else 400). `ready` → `handed_over`; 409 in another status | `order.handed_over` |
| `GET /warehouse/drivers` | `warehouse.work` | Users with the Driver role | — |

Wholesaler orders (step 5.6, migration 0059): a warehouse has a `kind` (`company` default, or `wholesaler`; set on `POST /warehouses`, never changed afterwards). Orders of a `wholesaler` warehouse have no preparation and no driver: `placed` → `dispatched`. The company warehouse flow ignores wholesaler warehouses. New role `WholesalerOfficer` (مسئول مذخر, scope `own`, no phone binding) with permission `wholesaler.work`; `POST /warehouses/{id}/staff` accepts a user with `wholesaler.work` for wholesaler warehouses (and `warehouse.work` for company ones).

| Endpoint | Permission / scope | Notes | Audit |
|---|---|---|---|
| `GET /wholesaler/warehouses` | `wholesaler.work`; own wholesalers | `[{id, code, name, openOrders}]` | — |
| `GET /wholesaler/orders?warehouseId=&status=&limit=` | same | Newest first; without `status` the open ones (`placed`); `status=dispatched` for the sent ones; lines with product, quantity, price, plus customer name, address, phone | — |
| `POST /wholesaler/orders/{orderId}/dispatch` | same | Body `{note?}`; `placed` → `dispatched`; 409 in another status; 404 for an order of another wholesaler | `order.dispatched` |

Cancelling (step 2.2) changes: the creator cancels while the order is `placed`; `orders.cancel` holders (sales manager, supervisor) cancel until the order is handed to a driver (`placed`, `preparing`, `ready`); a cancelled order cannot be worked on any more. The sellers' `GET /orders/{id}` now shows the prepared quantities, the driver and the steps too.

Tests: `WarehouseWork_StaffSeeOnlyTheirWarehouseOrdersAndMoveThemThroughPreparationToTheDriver`; app: `WarehouseTests`; panel: the Warehouse tab and the staff list in the Orders tab (`Panel.test.tsx`).

## Delivery by the driver (step 2.4)

Migration 0028: orders get the statuses `picked_up`, `on_the_way`, `delivered`, `delivery_failed` (and `receiver_name`, `delivery_note`, `failure_reason`); order lines get `delivered_quantity` and `delivery_reason`; `order_status_history` gets `latitude`, `longitude`, `accuracy_m`, `distance_m`; permission `delivery.work` (Driver role; also for existing companies). Owner decisions 2026-10-07: steps **handed over → picked up → on the way → delivered, or not delivered with a reason**; evidence is **the phone's GPS position and time plus the receiver's name and a note** (no photo or signature yet); when the customer got less than what the warehouse prepared the driver enters the **delivered quantity of each line with a reason** (audited; the order total does not change).

A driver sees and handles **only the orders assigned to them** (`driver_id`): any other order is 404, a user without `delivery.work` is 403. The position is evidence only: it is optional, nothing is refused for being far away, the server stores the distance to the customer when both positions are known, and a position the phone says is fake (`isMocked`) is refused (422 `GPS_MOCKED`); an impossible coordinate is 400.

| Route | Permission and scope | Behaviour | Audit |
| --- | --- | --- | --- |
| `GET /deliveries?status=&limit=&before=&beforeId=` | `delivery.work`; own orders | Newest first; without `status` the open ones (`handed_over`, `picked_up`, `on_the_way`); other values: `delivered`, `delivery_failed` | — |
| `GET /deliveries/{id}` | same | The order with lines (prepared and delivered quantities), `delivery` (customer phone, address, position, receiver, note, failure reason) and the steps with their evidence | — |
| `POST /deliveries/{id}/pickup` | same | Optional body `{location: {latitude, longitude, accuracyMeters, isMocked}}`. `handed_over` → `picked_up`; 409 in another status | `order.picked_up` |
| `POST /deliveries/{id}/depart` | same | Same body. `picked_up` → `on_the_way` | `order.on_the_way` |
| `POST /deliveries/{id}/deliver` | same | `receiverName` (1-100), optional `note`, `lines` (every line once: `lineNo`, `quantity` 0 up to the prepared quantity, `reason` when less), optional `location`. At least one line above 0 (else use `fail`). `on_the_way` → `delivered`; 400 / 409 | `order.delivered` (+ `order.delivery_shortage` when less was delivered) |
| `POST /deliveries/{id}/fail` | same | `reason` (1-300), optional `location`. From `handed_over`, `picked_up` or `on_the_way` → `delivery_failed` (final for now); 409 otherwise | `order.delivery_failed` |

The sellers' and warehouse's `GET /orders/{id}` and `GET /warehouse/orders/{id}` now also show the delivered quantities, the receiver, the failure reason and the evidence of each step. Managers cancel only until the order is handed to a driver (unchanged).

### Payments (plan step 2.5)

Permissions: `payments.record` (FinanceManager, SalesRep), `payments.manage` (FinanceManager), `payments.read` (FinanceManager, CompanyManager, SalesManager: all; Supervisor: the region). A payment belongs to a customer (inside the caller's area) and optionally to one of its orders (same customer, not cancelled, same currency). Methods `cash`, `fastpay`, `fib`, `other`; `fastpay` and `fib` need `reference`. Currency `IQD` or `USD`; amount above 0, at most 3 decimals. Status: `collected` (a visitor took the money) → `confirmed` (finance manager received it) → `voided` or `superseded` (corrected). Number `P-000001`.

| Route | Who | Rules | Audit |
|---|---|---|---|
| `POST /payments` | `payments.record` | `{customerId, orderId?, method, currency, amount, reference?, note?, clientRef}`. 201 `{id, number, status, replayed}`; 200 for a retry of the same `clientRef` (same user and customer, else 409). Status is `confirmed` when the caller also has `payments.manage`, else `collected`. 400 invalid or order mismatch, 404 customer outside the area or order missing | `payment.recorded` / `payment.recorded_confirmed` |
| `GET /payments?customerId=&status=&limit=&before=&beforeId=` | `payments.read` (all or region); a user who only records sees what they recorded | Newest first | — |
| `GET /payments/mine` | `payments.record` | What the caller recorded | — |
| `GET /payments/{id}` | same visibility | The payment with its history; 404 outside the visibility | — |
| `POST /payments/{id}/confirm` | `payments.manage` | `collected` → `confirmed`; 409 otherwise | `payment.confirmed` |
| `POST /payments/{id}/void` | `payments.manage`; a visitor only their own `collected` payment (403 once confirmed, 404 for another user's) | `{reason}` (1-500); `collected`/`confirmed` → `voided`; 409 otherwise | `payment.voided` |
| `POST /payments/{id}/correct` | `payments.manage` | `{orderId?, method, currency, amount, reference?, note?, reason}`; the old payment becomes `superseded`, a new `confirmed` payment points at it (`replacesPaymentId`); 409 if the old one is closed | `payment.corrected` |
| `GET /customers/{id}/balance` | `payments.read` or `payments.record`, customer inside the area | Per currency: `delivered` (delivered quantity x unit price of delivered orders), `confirmed`, `collected`, `balance` = delivered − confirmed | — |

Tests: `Delivery_DriverSeesOnlyAssignedOrdersAndRecordsPickupDeliveryOrFailureWithEvidence`; app: `DeliveryTests`; panel: the delivered/receiver/distance display in `Panel.test.tsx`.

### Pharma samples (plan step 2.6)

Permissions: `samples.record` (ScientificRep, SalesRep), `samples.read` (CompanyManager, SalesManager: all; Supervisor: the region; a user who only records sees what they recorded), `samples.manage` (SalesManager: void). Number `S-000001`. No stock or quota control yet.

| Route | Who | Rules | Audit |
|---|---|---|---|
| `POST /samples` | `samples.record` | `{customerId, visitId?, productId, quantity, recipientName?, batchNumber?, expiryDate?, note?, clientRef, location?}`. Customer inside the caller's area (404), product active (404 missing, 400 inactive), quantity above 0 with at most 3 decimals, `expiryDate` not before today (Iraq), the visit is the caller's own visit of this customer and not cancelled/failed. A fake position (`isMocked`) is 422 `GPS_MOCKED`; a position is otherwise only stored. 201 `{id, number, replayed}`; 200 for a retry of the same `clientRef` (same user and customer, else 409) | `sample.recorded` |
| `POST /samples` (panel, plan step 4.13) | Same route; migration 0044 also grants `samples.record` to SalesManager so a manager can record one from the panel (no position, no visit). | — | — | Panel tests. |
| `GET /samples?customerId=&productId=&limit=&before=&beforeId=` | `samples.read` or `samples.record` | Newest first, visibility as above | — |
| `GET /samples/{id}` | same | 404 outside the visibility | — |
| `POST /samples/{id}/void` | `samples.manage` | `{reason}` (1-500); `given` → `voided`; 409 when already voided | `sample.voided` |

### Specialised visit form (plan step 2.7)

Written beside the base report (unchanged). No new permission: the assignee writes, readers are the same as for the report (the assignee and `visits.write` scope over the assignee).

| Route | Who | Rules | Audit |
|---|---|---|---|
| `PUT /visits/{id}/report/details` | the visit's assignee, while `InVisit` (400 `INVALID_STATE` otherwise; another user 403) | Replaces the whole form. `purpose` (required: `product_intro`, `order_follow_up`, `collection`, `complaint`, `other`), `personMet`/`personRole` (≤100), `needs` (≤1000), `productIds` (≤50, distinct, active catalogue products), and by the visit's type: doctor `specialty` (≤100), `dailyPatients` (0-10000); pharmacy `monthlySales`, `competitors` (≤500); wholesaler `monthlyVolume`, `paymentTerms` (≤500). A field of another type is refused (400), not ignored. `currency` (`IQD`/`USD`) is required exactly when `monthlySales` or `monthlyVolume` is sent. 204 | `visit.report_details_saved` |
| `GET /visits/{id}/report/details` | same readers as the report | The form with `visitType` and the products (code, name); 404 when none was saved or the caller may not read the visit | — |

### Follow-up (plan step 2.8)

Permissions: `followups.create` (SalesRep, ScientificRep, SalesManager, Supervisor), `followups.manage` (SalesManager, Supervisor: for users in their area), `followups.read` (SalesManager, CompanyManager: all; Supervisor: the region). Everybody also sees the follow-ups they are responsible for or made. Status `pending` → `in_progress` → `completed` (result note) or `cancelled` (reason). `isOverdue` = open and the due day (Iraq) has passed; it is worked out when read.

| Route | Who | Rules | Audit |
|---|---|---|---|
| `POST /followups` | `followups.create` | `{customerId, subject (1-200), dueDate (not before today, Iraq), assigneeId?, clientRef}`. The customer must be in the caller's area (404). `assigneeId` other than the caller needs `followups.manage` (403) and a user in the caller's area (404). 201 `{id, replayed}`; 200 for a retry of the same `clientRef` (same creator and customer, else 409) | `followup.created` |
| `GET /followups?customerId=&assigneeId=&status=&overdue=&limit=` | `followups.read` (all/region), else `followups.create` (own) | Ordered by due day; limit 1-200 (default 100) | — |
| `GET /followups/{id}` | the responsible user, the creator, or a reader/manager covering the responsible user | 404 otherwise | — |
| `POST /followups/{id}/start` | the responsible user only (403 for others who can see it, 404 for the rest) | `pending` → `in_progress`; 409 otherwise | `followup.started` |
| `POST /followups/{id}/complete` | the responsible user only | `{resultNote}` (1-500); `pending`/`in_progress` → `completed`; 409 otherwise | `followup.completed` |
| `POST /followups/{id}/cancel` | the creator, or `followups.manage` covering the responsible user | `{reason}` (1-500); open → `cancelled`; 409 otherwise | `followup.cancelled` |
| `POST /followups/{id}/reassign` | `followups.manage` covering the responsible user | `{assigneeId}` in the manager's area (404 otherwise); open only (409) | `followup.reassigned` |

### Alerts (plan step 2.9)

Permissions: `alerts.read` (SalesManager, CompanyManager: all; Supervisor: the region of the user the alert is about), `alerts.security.read` (SalesManager, CompanyManager), `alerts.handle` (SalesManager, Supervisor; a security alert also needs `alerts.security.read`). Categories `operational` and `security` are separated by these permissions. Kinds: operational `followup_overdue`, `visit_delayed` (planned visit of today not started 30 minutes after its planned time), `visit_long_stay` (in a visit 90 minutes; `detail` = `report_saved`/`report_missing`), `tracking_gap` (open shift, no point for 15 minutes); security `new_device` (phone waiting for approval), `gps_mocked` (check-in/out or tracking position the phone says is fake). One event = one alert (unique dedupe key; never made again, even after handling). Status `new` → `seen` → `handled`.

| Route | Who | Rules | Audit |
|---|---|---|---|
| `GET /alerts?category=&status=&kind=&limit=` | `alerts.read` and/or `alerts.security.read` | Only the categories/areas the caller may see; unhandled first, newest first; limit 1-200 | — |
| `GET /alerts/count` | same | `{new}` = new alerts the caller may see | — |
| `GET /settings/shift-alerts` | Any alert reader (`alerts.read` or `alerts.security.read`) | `{shiftWindowHours, stationaryMinutes}` of the company (defaults 3 and 20) | — |
| `PUT /settings/shift-alerts` | `alerts.handle` with tenant-wide scope (the sales manager); a supervisor gets 403 | Body `{shiftWindowHours 1-12, stationaryMinutes 5-120}`; 204, 400, 403 | `shift_alert_settings.changed` |
| `GET /alerts/{id}` | same | 404 outside the caller's categories/area | — |
| `POST /alerts/{id}/seen` | `alerts.handle` | `new` → `seen`; 409 otherwise; 404 outside the area | `alert.seen` |
| `POST /alerts/{id}/handle` | `alerts.handle` | `{note}` (1-500); `new`/`seen` → `handled`; 409 when handled | `alert.handled` |
| `POST /alerts/scan` | `alerts.handle` | Looks at the caller's company now and makes the missing alerts; `{created}`. The background scan does the same every `Alerts:ScanIntervalSeconds` (default 300, 0 = off; first scan after one interval) | — |

### Anti-fraud (plan step 2.10)

No new route or permission: the new signals are alert kinds made by the alert scan (see 2.9 and `docs/ANTIFRAUD.md`), all in the **security** category (`alerts.security.read`): `impossible_speed`, `clock_tamper`, `outside_area`, `poor_accuracy`, `device_mismatch`. Changes to the alert routes:

| Route | Change |
|---|---|
| `GET /alerts`, `GET /alerts/{id}` | The alert also carries `evidence` (JSON text kept by the scan) and `handledOutcome` |
| `POST /alerts/{id}/handle` | Body `{note, outcome?}`. `outcome` (`valid`, `suspicious`, `fraud`) is **required** for `gps_mocked`, `impossible_speed`, `clock_tamper`, `outside_area`, `poor_accuracy`, `device_mismatch` and refused (400) for other kinds; an unknown value is 400 |

### Route history, playback and the customer map (plan step 2.11, decision D6)

Retention: exact tracking points are kept 90 days. The background job (same loop as the alert scan) deletes the points of shifts that ended more than 90 days ago after writing `shift_summaries`. Stops are computed when a route is read: at least 5 minutes within 50 m of the first point of the stop; the nearest customer within 150 m is named.

| Route | Who | Rules | Audit |
|---|---|---|---|
| `GET /tracking/users` | `tracking.read` | Active users inside the caller's area that have had a shift | — |
| `GET /tracking/history?userId=&date=` | `tracking.read` and the user inside the caller's area (404 otherwise; 403 for the summary-only roles and visitors) | `date` (Iraq day) not in the future and not older than 90 days (400). Returns `shifts` (with `pointsPurged`), `points` (time order, at most 20000, `truncated`), `stops`, `events` (check-in/out with the customer, radius, accepted or refused) | `tracking.history_viewed` (entity `userId:date`) |
| `GET /customers/map` | `customers.read` | Located customers of the caller's area (at most 5000): id, code, name, type, position, `geofenceRadiusMeters` (default 20). The panel clusters them and draws the check-in circle from zoom 14 | — |

### Reports and KPIs (plan step 2.12)

Permission `reports.read` (CompanyManager, SalesManager: all users; Supervisor: the users of the region). Definitions of every number: `docs/KPI.md`.

| Route | Who | Rules |
|---|---|---|
| `GET /reports/kpis?from=&to=&regionId=&teamId=&customerType=` | `reports.read` | `from`/`to` are Iraq days (`to` not before `from`, at most 366 days; else 400). Returns `total` and `users` (one row each, users without data left out) with `counts` (numerators, denominators, money per currency) and `rates` (percent, `null` when the denominator is 0), and `alerts` by kind with handled and review results, only the categories the caller may read (`alerts.read`, `alerts.security.read`) |

### Offline work (plan step 2.13, `docs/OFFLINE.md`)

No new route or permission. Changes to existing routes, all used by the phone for work kept without internet:

| Route | Change |
|---|---|
| `POST /payments` | Optional `detachOrderIfInvalid`. When true and the order is cancelled, another customer's, in another currency or missing, the payment is recorded without the order: 201 with `detached: true`, audit `payment.recorded_detached`. Amount and customer rules still apply |
| `POST /samples` | Optional `detachVisitIfInvalid`: a cancelled/failed visit (or one that is not the caller's) is dropped; answer `detached: true` |
| `POST /orders` | Optional `detachVisitIfInvalid`: the order is placed without a cancelled/failed visit; answer `visitDetached: true`. The server prices it at the day it arrives |
| `POST /followups/{id}/start`, `/complete`, `/cancel` | A step sent again after a lost answer is a success without a second write (start of an in-progress follow-up; complete / cancel with the same note / reason). Another status or text stays 409 |

## Company directory (plan step 4.2)

| Route | Access | Notes |
|---|---|---|
| `GET /company-config/{code}` | public (no token), global rate limit | Returns `tenantCode`, `name`, `apiBaseUrl` (null = this server), `supportPhone` for an active company; unknown/inactive/invalid code → 404. Public facts only. Excluded from the OpenAPI contract. |

### Anti-fraud from visits (plan step 4.8)

No new route or permission. The alert scan makes three more kinds: `impossible_travel` and `short_stay` (security, `alerts.security.read`; `outcome` is required when handled, like the kinds of 2.10) and `low_coverage` (operational, `alerts.read`; no outcome). See `docs/ANTIFRAUD.md`.

## Message channels (plan step 3.4, decision D17)

Optional and off until `Channels:Enabled` and a bot token are set (`docs/CHANNELS.md`). Every user sees and changes only their own link; no permission key.

| Route | Access | Notes |
|---|---|---|
| `GET /channels` | any signed-in user | `{enabled, botUsername, telegramLinked}`; `botUsername` is null while the channel is off |
| `POST /channels/telegram/link` | any signed-in user | One-time code `<company code>_<12 random>`, valid 15 minutes (only its hash is stored). 503 while the channel is off or has no token |
| `POST /channels/telegram/unlink` | any signed-in user | 204; 404 when there was no link or waiting code. Audit `channel.telegram_unlinked` (and `channel.telegram_linked` when the bot confirms the code) |
