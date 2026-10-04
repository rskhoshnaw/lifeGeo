# LifeDrop GeoTrack — Product Requirements (working English edition)

Updated: 2026-10-04.

**Authority.** The binding source is `docs/سند نیازمندی محصول LifeDrop.docx`, version 2.0. This file restates it in English for engineering use and records product decisions made since (D1–D11). If the two disagree, the Persian document plus the decision log in section 15 wins, and this file must be corrected.

**Related files.** Remaining work and step order: `docs/IMPLEMENTATION-PLAN.md`. Current evidence: `memory-bank/phaseStatus.md`. Coding rules: `docs/06-CODING-CONVENTIONS.md`.

**History.** This file previously held a Node/Express/Supabase execution plan for Google AI Studio (prompts P0–P12 and M0–M5). That stack was not adopted; the product is built on ASP.NET Core, PostgreSQL/PostGIS, .NET MAUI and a React + TypeScript panel. The old text remains in git history. Useful lessons from it are kept in section 16.

---

## 1. Purpose and scope
LifeDrop is a field-force system for pharmaceutical distribution companies. It connects one cycle:

Planning → Assignment → Travel → Arrival → Check-in → Visit, report and order → Check-out → Follow-up → Analysis

| Component | Technology | Users |
| --- | --- | --- |
| Mobile app | .NET MAUI, Android first | Sales and scientific representatives; later warehouse staff and drivers |
| Web panel | React + TypeScript (D10) | Managers, supervisors, finance, administrators |
| Backend | ASP.NET Core API, PostgreSQL + PostGIS | Access control, location validation, sync, audit |

Initial deployment: about 50 field staff in Iraq (Erbil, Baghdad, Sulaymaniyah, Kirkuk, Duhok, Nineveh, Salah al-Din). Must grow to 150 without a fundamental redesign. UI languages: Sorani Kurdish, Arabic, Persian (RTL) and English (LTR).

**Measurable goals**
1. A manager defines a daily plan (optional or mandatory) and assigns it to an authorised person.
2. A representative sees the plan and moves through the visit cycle without skipping mandatory states.
3. Check-in and check-out are accepted only when identity, state, ownership and location rules pass.
4. During connectivity loss, permitted data is not lost and "pending sync" is shown honestly.
5. A manager can search sensitive events in the audit log.
6. Every management number is computed from real data, never demo data presented as operational.

**Out of MVP:** AI route optimisation, general ERP/SMS/WhatsApp integration, full report builder, advanced analytics, commercial SaaS for other companies.

## 2. Non-negotiable principles
- **Tenant isolation:** each company's data is read and changed only within its tenant. Super Admin cross-tenant access is explicit and audited.
- **Server-side control:** hiding a button never replaces an API permission check.
- **Audit:** sensitive operations record actor, time, entity and permitted changes. Audit entries cannot be deleted from the product.
- **Honest state:** "saved locally", "sent", "accepted by server" and "rejected" are four different states and are shown differently.
- **No sensitive data in logs:** passwords, tokens, precise coordinates, personal data and secrets are never printed.
- **Preserve existing work:** this document does not authorise rewriting architecture or deleting existing code or data without reason.
- **Definition of done:** a green build alone is not a working feature; each capability is verified at unit, integration and real-device level as appropriate.

## 3. Roles, access, regions and teams
**Roles:** Super Admin, Company Manager, Sales Manager, Supervisor / Area Manager, Finance Manager, Sales Representative, Scientific Representative, Warehouse Manager, Driver.

| Capability | Base roles | Constraint |
| --- | --- | --- |
| Tenant management | Super Admin | All changes audited |
| User management | Super Admin, Sales Manager, Supervisor | The last two only within their team/region |
| Region and team management | Sales Manager | Defines regions and assigns each team to one region (D11); audited |
| Visit planning and assignment | Super Admin, Sales Manager, Supervisor | Supervisor only within scope; representative only for own visit with explicit permission |
| Live tracking | Super Admin, Sales Manager, Supervisor, Finance Manager | Finance access is kept; level of detail per company policy (D3) |
| Check-in / check-out | Assigned representative | Ownership, state, device and GPS checked on the server |
| Customers | Authorised management roles; limited add for representatives | Location changes audited; out-of-region rule below |
| Orders | Sales, finance, warehouse, delivery by duty | Each role sees only orders in its scope |
| Payments | Finance Manager and explicitly authorised roles | Create/correct with audit and financial control |
| Audit log view | Super Admin; limited for Sales and Finance Managers | Visible scope and fields defined |
| Device approval, kill session | Super Admin and authorised managers | Only for authorised users/teams; immediate and audited |
| Backup deletion | Super Admin; Sales Manager only with explicit grant | Not a default right; re-confirmation, audit, retention policy (D4) |

**Region and team model (D11).** Each team operates in exactly one region. The Sales Manager defines regions and assigns them to teams. Customers and visits carry a region; supervisors and representatives are limited to their team's region on the server.

**Out-of-region customers (D11).** A representative may register a customer found outside their region. The customer is saved as *pending, out of region* and appears in an approval queue. After an authorised manager (Sales Manager or Supervisor of the target region) approves it, the customer is assigned to that representative. Approval and rejection are audited. Until approved, the customer cannot be used for geofenced check-in.

Permissions are independent keys (`resource.action`); a role name alone never replaces ownership, region, tenant and operation rules. No management role may delete audit entries.

**Acceptance:** every endpoint and page has tests for allowed, wrong role, other tenant, other region and other owner.

## 4. Authentication, device and session
- Login with the user's assigned identifier and password; passwords stored only as a secure hash.
- Each mobile account is bound to an approved device. Changing device requires a recorded approval.
- Logout invalidates the active session; administrative kill session cuts the target's sessions without reinstalling the app.
- Expired or revoked sessions get a defined response and the app returns to a safe login state.
- Failed login, successful login, device approval, logout and kill session are audited; passwords and tokens never.
- Offline with an expired session: viewing permitted local data may continue by policy, but sensitive actions are never shown as server-confirmed.

**Acceptance:** an unapproved device cannot use operational APIs; kill session works without reinstall; logout really leaves the operational environment.

## 5. Visit lifecycle
**Planning.** A visit has at least tenant, customer, visit type, assignee, scheduled day/time, priority and status. A customer may have several visits a day. An unplanned visit needs permission and a recorded reason. Managers create, assign and reassign; reassignment is audited. A representative's daily plan returns only their visits; a day without visits returns 200 with an empty list.

**States.** `Planned → EnRoute → Arrived → InVisit → Completed`.
- Start travel: only `Planned → EnRoute`. Arrive: only `EnRoute → Arrived`.
- Check-in: only from `Arrived`, after identity, ownership, device and location checks → `InVisit`.
- Report: saved in a permitted state with valid data.
- Check-out: only from `InVisit`, with a saved valid report and valid GPS inside the customer geofence (D2) → `Completed`.
- `Failed` and `Cancelled` (D7): allowed from `Planned`, `EnRoute`, `Arrived` and `InVisit` with a meaningful reason. The assigned representative may report `Failed`; only a manager with permission may `Cancel`. `Completed`, `Failed` and `Cancelled` are terminal.
- No client may jump directly to `InVisit` or `Completed`. Network retries must not create contradictory records or transitions; the UI disables the button while sending and the server checks state independently.

**Visit report.** The base form has an explicit outcome and a meaningful note. Outcome values are shared and versioned between mobile and server. An outcome claiming an order was placed is allowed only if a real linked order exists. Specialised forms for doctor, pharmacy and wholesaler (purpose, person met, products presented, needs, outcome, note, follow-up time; photo and signature by policy) come in Phase 2.

**Acceptance:** empty, malformed, out-of-list or invalid reports are rejected in the UI and via direct API. A failed report save never starts check-out. If the report saved but check-out failed, the app shows partial success and allows retrying check-out.

## 6. GPS, geofence, tracking and anti-fraud
**Check-in and check-out (D2).**
- Customer coordinates must be valid and carry source and timestamp.
- Default radius 20 m; an authorised manager may change it per customer or policy.
- The phone sends coordinates with time and accuracy. Distance and the final decision are computed on the server.
- **Both check-in and check-out require fresh, valid GPS inside the customer radius.** Recording an arbitrary location is not enough. Out-of-range, invalid, stale, low-accuracy or mock-flagged positions are rejected, recorded with a reason and audited, never silently dropped.

**Shift tracking.** Login and tracking are separate. Proposed (D1, open): the session starts at login, continuous location is recorded only during an active shift, and check-in/out follow their own permissions. Target freshness of a live point on a good connection is about 30 s, configurable. The panel shows the last-update time and never labels an old point as live. History, stops and playback follow a retention policy (D6).

Tracking must keep working with the screen off. Before building full tracking, test background location on real Android phones of the brands in use (screen off in pocket, phone idle on a table, app swiped away, battery saver on/off; Android 14+ needs a `location` foreground service). The product owner sets the acceptance threshold; suggested: at least 90 % of expected points and no gap above 5 minutes while moving.

**Anti-fraud.** Signals: mock location, GPS off, impossible jumps or speed, time tampering, disconnection, long gaps, leaving the area. Each signal has a computed definition, threshold, action and human review path. Fake-GPS detection on tampered devices is not absolute; the product uses layered controls and keeps evidence instead of claiming to be cheat-proof.

**Acceptance:** no direct API request with invalid coordinates, wrong owner or wrong state can produce a valid check-in or check-out.

## 7. Offline and sync
- Permitted data for the workday is stored on the device in protected form. Target: up to 24 hours offline; each operation is defined as offline-allowed or online-only.
- Offline changes go to a durable queue with unique ID, device time, dependency order and send status. Resend after reconnecting is safe and idempotent where possible.
- Conflicts are not resolved by "last write wins" for every entity; visit ownership, orders, payments and check-in/out have specific rules. Offline check-in/out acceptance is decision D5 and must still satisfy D2 on the server.
- Server errors and rejections are visible in the app; local data is not deleted without a defined decision.

**Acceptance:** turning off the internet, closing and reopening the app and reconnecting neither loses nor duplicates permitted operations. Anything not yet server-confirmed is never labelled completed.

## 8. Customers, products, orders, delivery and payments
- **Customer:** type, code, name, contact, address, city/region, coordinates, status and follow-up owner. A customer without GPS has a defined status; before geofenced check-in, its location is set or corrected with permission and audit. New customers added by representatives appear in a dedicated manager list (see D11 for out-of-region).
- **Products and prices:** active products, unit, base price and region/customer price rules with validity dates. Order totals are computed from server-approved prices, never from prices sent by the phone.
- **Orders:** linked to a customer and, where applicable, a visit; items, quantity, unit, price, currency and status. An order is "placed" only after the server accepts it. The destination warehouse is defined and in scope.
- **Warehouse and delivery:** warehouse staff see only their warehouse/region and record preparation and handover; drivers see only assigned deliveries and record status with time and evidence. Status changes and item discrepancies are audited.
- **Payments:** currency, amount, customer, order/invoice reference, time and recording actor. Corrections keep an auditable history instead of overwriting.

## 9. Follow-up, alerts and notifications
- Follow-up: subject, owner, related customer/visit, due date, states `Pending → InProgress → Completed`, derived `Overdue`.
- Alerts: delay, long stop, GPS/internet loss, tracking gap, leaving the area, incomplete report, overdue follow-up, new device, suspicious behaviour. Each has source, time, severity, seen/handled state and authorised audience. In-panel notifications first; push and SMS later.

**Acceptance:** repeated alerts for one event do not flood managers; security and operational alerts are separable.

## 10. Maps and reports
- Target: OSM data with MapLibre-compatible technology. Show customers and permitted staff positions with last-update time, clustering, geofences and history, and usability on weak connections. Offline maps, self-hosted routing and geocoding come later, after testing Iraq coverage.
- Reports use real data, filter by time, region, team and customer type, and respect permissions. Base KPIs: planned/completed/failed visits, delays, new customers, orders, overdue follow-ups, each with a documented numerator and denominator.
- A Smart TV view is a read-only, limited management view with privacy controls.

## 11. Data and API contract
- PostgreSQL/PostGIS. Target entities: Tenant, User, Role/Permission, Region, Team, Customer, Visit, Visit Report, Product, Price Rule, Order/Item, Tracking Point, Follow-up, Sample Distribution, Payment, Alert, Audit Log, Report Schedule.
- Every tenant-owned record has `tenant_id` with database constraints and RLS. Times stored in UTC; "today" for Iraq is the local day (`Asia/Baghdad`). Coordinates use a geographic type with a defined SRID. Statuses have versioned contracts. Soft delete for retained data. Schema changes only through migrations.
- Each endpoint's contract (roles, tenant/region/ownership scope, valid input, response, errors, idempotency, audit effect) is fixed before the feature is built. Existing routes are the base; no parallel paths such as `/api/api/...`.

**Acceptance:** invalid input 400, missing permission 403, invalid session 401, record not in scope 404, state conflict 409 or a documented contract error; predictable errors are never 500. An empty list is a successful empty array.

## 12. Security, privacy and backup
- TLS for all non-development traffic. Location access limited by role, scope and need, and viewing is recorded.
- Minimal, filtered sensitive data in responses and audit.
- Audit for login, device approval, kill session and changes to users, customers, regions/teams, visits, orders, payments and backup deletion. Audit is not deletable from the UI or API; database protection and retention are tested separately.
- Backups are periodic, off-site and restore-tested; "a backup exists" without a restore test is not acceptance. Recovery objectives, retention and the approver of deletions are set before final deployment (D4, D9).

## 13. Quality and non-functional requirements
- Usable on common Android phones over unstable connections.
- RTL Kurdish, Arabic and Persian and LTR English without broken forms.
- GPS sampling controls battery use, measured on real devices.
- Live-tracking freshness targeted and measured; disconnection shown clearly.
- Sensitive operations resist repeated requests, disconnection during send and late responses.
- Test data is separated from real data. No feature is declared ready without recorded acceptance evidence.

## 14. Phases and exit criteria
| Phase | Content | Exit |
| --- | --- | --- |
| 0 — Foundation | Auth, tenant, RBAC with region/team, migrations, safe seed data, API contract, base audit, isolated test environment, CI | Login/logout, tenant and role limits, migrations from an empty database and base tests pass |
| 1 — Operational visit MVP | Daily planning and assignment, real plan on mobile, state cycle, shift and basic tracking, geofenced check-in/out, base report, new customers, React operations panel, related audit, honest basic offline | One real test visit from creation to completion on a device and isolated environment, with API, database, audit, error and retry checks |
| 2 — Sales and operations | Catalogue and pricing, orders, warehouse and delivery, payments, samples, follow-up, reports, alerts, extended offline | Order-to-delivery and financial report with role/region/tenant control and independent integration tests |
| 3 — Scale, SaaS, automation | Commercial multi-tenancy, form/report builder, report scheduling, advanced routing, analytics, integrations, AI | Cross-tenant security, backup restore, capacity and observability tested |

Step-level breakdown: `docs/IMPLEMENTATION-PLAN.md`.

## 15. Decision log
| ID | Topic | Status | Decision / current proposal |
| --- | --- | --- | --- |
| D1 | When tracking starts | Open | Explicit shift start after login |
| D2 | Check-out location rule | **Decided 2026-10-04** | Fresh valid GPS inside the customer radius, same anti-fraud checks as check-in |
| D3 | Finance Manager tracking detail | Open | Access kept; precise, summary or finance-scoped view to be decided |
| D4 | Backup deletion by Sales Manager | Open | Optional explicit grant; which copies and what confirmation |
| D5 | Offline check-in/check-out | Open | Temporary unconfirmed record; whether late acceptance is allowed and how it is validated against D2 |
| D6 | GPS history retention | Open | Short-term playback plus long-term reports; earlier proposal 90 days, needs privacy decision and staff consent |
| D7 | Failed and Cancelled | **Decided** | From all four active states with a reason; Failed by assignee, Cancelled by manager; terminal |
| D8 | iOS timing | Open | Android validates the MVP; iOS needs an Apple account and build environment |
| D9 | Backup RPO/RTO | Open | Business-acceptable values before launch |
| D10 | Web panel technology | **Decided 2026-10-04** | React + TypeScript in `src/LifeDrop.Web`; inherited JSX/Express template removed |
| D11 | Regions, teams, out-of-region customers | **Decided 2026-10-04** | One region per team, set by the Sales Manager; out-of-region customers registered by a representative need manager approval, then belong to that representative |
| — | Kurdish variant | Open | Sorani (`ckb`) assumed; confirm |

## 16. Engineering lessons carried forward
1. Verify, do not assume: an agent saying it "checked" something is not evidence; read the source or the run output.
2. Never replace or regenerate data to make a test pass; one overwrite already lost a real record.
3. Do not edit config, migrations or tests outside the step's scope without asking.
4. Never invent secrets or write them into tracked files.
5. Concurrency limits need real concurrency tests; a rate limit once rejected far more requests than intended until fixed and re-tested.
6. Avoid giant files; split UI by feature so a change cannot break unrelated screens.
7. Product text must come from the PRD, not generated placeholder wording.
