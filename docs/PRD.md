# LifeDrop — Execution Plan and Prompts (Version 2)

Updated: September 30 , 2026
This file is a complete replacement of the previous file. Prompts P0 to P3d have been done and only their summaries are given; their full text remains in the old file. Give the English prompts exactly in AI Studio .

## List
1. Changes from the previous version
2. AI Studio's capabilities and limitations
3. Lessons learned so far
4. Final architecture
5. How Tokens Work and Save
6. Project Rules File (Version 2)
7. Progress status and open manual tasks
8. Web and Backend Prompts (P6 to P12)
9. Mobile app (Android and iPhone)
10. Open decisions

---

## 1. Changes from the previous version

- ** Native mobile app required . ** Tracking with the screen off is a mandatory requirement and the web cannot fulfill it. Visitors have both Android and iPhone , so the app must be built for both .
- ** Work is in two parallel directions :* * Web and backend in AI Studio ; mobile app outside of AI Studio with separate coding tools .
- ** Two new backend stages :* * User (P4b) and device (P8b) management . Without P4b, there is no way to create a user other than the primary admin .
- **P6 rewritten ** And it comes after P3d and before P4 .
- ** Rules 11 to 17 ** Added to the rules file (RLS , compact reporting, Migration immutability , ` asyncHandler ` , etc. ).
- ** Token saving tools :* * CI on GitHub , compact report format, `STATE.md` file , and sending only `server/` and `tests/` folders.
- **PWA and offline web removed . ** The offline queue is created in the mobile app .

---

## 2. AI Studio's capabilities and limitations

### What it does well
| Capability | Explanation |
|---|---|
| React web app + Node.js server | Automatic installation of packages npm , Vite for Frontend |
| Secrets | Server side only |
| External Database | Cloud Run container connects to any accessible database ( ours : Supabase Postgres + PostGIS ) |
| Multi-file editing | with the limitations mentioned in section 3 |
| GitHub Two-Way | Push/Pull and Revert |
| Deploy | Cloud Run ; Starter Tier up to 2 services and not available for Workspace or previous Billing accounts |
| API , Migration , Testing | Should report actual test output |
Multilingual UI | i18n , RTL , Forms and Tables, Google Maps |

### What not to give it
| Subject | Reason | Decision |
|---|---|---|
| ** Mobile App ** (Background GPS , Offline Queue, Device Binding , Push) | Android app in AI Studio is client-side only, no server and secrets , emulator without real GPS and Google Play Services , single activity only, release only Internal Testing. iPhone is not covered at all . | App outside of AI Studio ; AI Studio only builds API and ` openapi.yaml ` |
| ** Web Tracking with Screen Off ** | Browser only gives GPS when screen is open and on; Mock Location is also not detectable | Native app |
| Test on real phone | Container and emulator only | Yourself on real phones |
| Backup/Restore , RPO/RTO , Load Testing | Infrastructure | Database Service and Technical Team |
| Kurdish/Arabic/Persian translation | May be incorrect | Human review |
| Security Audit | Writing Auth is not the same as Auth being secure | Independent review after each security step |
Default Firebase Auth | Google Login Only | Custom Auth on Postgres ( Done ) |
| SMS/WhatsApp/ERP | Outside MVP | Don't |

---

## 3. Lessons we've learned so far

** From early experiments ( Remixed.pdf)* *
1. Fake logo : Agent did not find the file and drew the SVG itself . The file must be added manually .
2. Create new demo data instead of deleting old data .
3. The record `nyc-19` was lost when overwriting the data file .
4. A superficial verification with a `curl | grep`.
5. ` App.tsx` is about 3100 lines long and any major rewrite would put it at risk .

** From stages P0 to P3d**
6. Agent sometimes calls something it hasn't seen "seen." Don't take the preview's description seriously; see for yourself .
7. Generated dummy text about the product area ( e.g. “Blood Distribution”) .
8. Edited the build file or old file without asking ( `.env ` , ` .env .example ` , `0003_rls.ts` , `tests/ dbTest.test.ts `).
9. He created a secret (`SESSION_PEPPER`) and wrote it in ` .env` .
10. Race in Entry Limit: With a limit of 5 people, 17 guesses were rejected. It was fixed with Advisory lock and exactly 5 and 20 were confirmed three times in a row .
11. The locking solution had a risk of pool starvation ; it was closed with ` lock_timeout ` and dedicated test .
12. ` window.fetch` error in iframe Preview; correct pattern: explicit ` authFetch ` function , without touching the global `fetch` .

---

## 4. Final Architecture

```
   Managers/Admin/Visitors' Office/Drivers (Android + iPhone)
   Responsive Web (AI Studio) Native/Multi-platform App ( Outside of AI Studio)
\ /
\ /
+----- / api /v1 (Express , Cloud Run) -----+
|
              Supabase Postgres + PostGIS (RLS enabled )
```

- ** Tenant Isolation ** In the server middleware and based on ` users.tenant _id ` ( not `sessions`). Global access ` super_admin ` is only from one path and is always audited .
- ** Auth:* * Username/Password with scrypt , obfuscated token in `Authorization` header (in database just hash), no cookie. Mobile clients with Device Binding.
- ** Contract :* * `docs/ openapi.yaml ` The only source of API for the mobile app .
- ** Time :* * UTC ; "Today" based on `Asia/Baghdad`.
- ** Languages : * fa , ar , ckb ( Sorani ) , en .
- ** Old Planner ** Under the `/legacy` path and labeled ``Demo data`` ; it remains intact until replaced .
- ** CI:* * GitHub Actions with real Postgres ; tests never run on the main database unless explicitly flagged .

---

## 5. How Tokens Work and Save

1. ** Each prompt has one step and one Checkpoint.* * After each step , push and tag (`step-04-rbac`).
2. Each prompt starts with "Just give me the program" and no files are changed until you say "go" .
3. **Agent compresses the report ** (Rule 12) and writes the raw output to `docs/evidence/<step>.txt` .
4. **CI on GitHub** Confirms correctness. Just give me "green/red" and if red, the text of the same step .
5. ** Security Steps ** Give for review : P4 , P4b , P5 , P8b , P9 , P10. Just ZIP the `server/` and `tests/` folders . The rest of the steps are sufficient with green CI and compact reporting .
6. ** New dialogue with `STATE.md` and `PRD_LifeDrop.md`** When this conversation got heavy .
7. ** Summary Builder (for non-security steps only ) :* * Run the Agent report in a new AI Studio dialog with this command and only get its output :

```
Extract from the report below ONLY: (1) changed files, (2) numeric test/lint/build results,
(3) claims NOT backed by a pasted command output, (4) failures or skipped items.
Do not add opinions. Keep under 25 lines. Never include secrets.
```

Third-party sites are not recommended, as the reports contain internal details .

---

## 6. Project Rules File (Version 2 )

Replace this text with `docs/PROJECT_RULES.md` .

```
# LifeDrop — Project Rules (read before every task)

Product: LifeDrop is a field-force, customer-visit, tracking and sales management
system for pharmaceutical distributors in Iraq (~50 users, growth to 150).
It is NOT a pet-grooming, blood-delivery or fleet app. Pet-grooming code is legacy and
must not be extended.

Sources of truth: docs/PRD.md and docs/STATE.md. If a task conflicts with them, stop
and report.

Hard rules
1. Smallest possible change. Prefer NEW files. Never rewrite or reformat an existing
   file wholesale. Do not touch src/App.tsx, GmpMapCanvas, tspSolver, or legacy
   components unless the task names them explicitly.
2. Before editing, list the exact files you will change. After editing, re-open each
   changed file and show the changed regions.
3. No sample/demo/mock data as operational data. Empty states must be honest.
   The only allowed seed is one Super Admin created from secrets.
4. Do not invent endpoints, fields, or response shapes. Read the real code first.
5. Server enforces auth, tenant scope, role/permission, ownership and state
   transitions. Hiding a button is never access control.
6. Never print passwords, tokens, secrets or exact coordinates in logs.
7. API errors: 400 invalid input, 401 invalid session, 403 forbidden,
   404 not found in scope, 409 state conflict, 429 rate limited. Empty list = 200 with [].
8. Times stored in UTC; "today" is computed in Asia/Baghdad.
9. Never claim completion from a written summary or a successful build alone. Never
   describe what the UI looks like unless you actually operated it; otherwise list what
   I must check by hand.
10. If something is impossible or blocked, stop and report the exact blocker.
    Do not create mock data or placeholder success screens.
11. Every new table must ENABLE ROW LEVEL SECURITY in its own migration.
12. Final report format (max 40 lines). Never paste full code or full verbose test output in
    chat. Write raw command outputs to docs/evidence/<task>.txt (no secrets in it) and reply
    ONLY with: files created/changed (one path per line); commands run with numeric
    results; every claim you could NOT verify; anything failed or skipped.
13. Migrations already applied anywhere are IMMUTABLE. Never edit an existing migration
    file. Schema changes go into a NEW numbered migration. State which migration files you
    will touch before editing.
14. Every route under /api/v1 except health and login uses requireAuth and
    requirePermission. Every async handler is wrapped in asyncHandler. tenant scope
    comes from req.auth (loaded from users), never from request input.
15. Do not create files outside the paths listed in the task. If you believe an extra
    file is needed, stop and ask.
16. Never create, modify or print secrets. Never create .env files. Report presence of
    secrets only as present/absent.
17. Tests run against TEST_DATABASE_URL or CI only, never against the main database.
```

---

## ۷. وضعیت پیشرفت و کارهای دستی باز

| مرحله | خلاصه | وضعیت |
|---|---|---|
| P0 | Read-only audit | Done and approved |
| P1 | Demo Brand, Logo, Label Cleanup | Done and Approved |
| P2 and P2b | Postgres , Migration , Seed , TLS with CA | On Supabase Real confirmed |
| P3 and P3b | Auth , Session , Legacy Path Guard, 28 tests | Done |
| P3c | Fix Race , Audit , RLS , Error handler , tenant from users , kill-session | 36 Test; Race fixed in 3 runs |
| P3d | CI , rule 12 , ` lock_timeout ` , test on separate database | 43 Test passed; CI on GitHub not yet implemented |

** Open Handicrafts (in order) * *
1. Push to GitHub and see the Actions result. It turned green : tag `step-03-auth-reviewed`.
2. Supabase → Advisors → Security Advisor: RLS warning should not be .
3. A project Supabase Separate (` lifedrop -test`) with PostGIS and Secret `TEST_DATABASE_URL`.
4. Get the diff file `0003_rls.ts` from GitHub (this file has been edited after implementation) and change the first 20 lines of `tests/ dbTest.test.ts` to .
5. Open the `docs/evidence/p3d.txt` file and make sure it does not contain a password, database address, or Pepper .
6. After the first deployment : `select count( distinct ip_hash ) from sessions;` After logging in from two different networks. If it was 1 , we need to set `TRUST_PROXY_HOPS` .
7. Try the preview manually: Login, Optimize Path, Logout, Refresh , Logo in Dark Mode .

---

## 8. Web and Backend Prompts

Order : **P6 → P4 → P4b → P5 → P7 → P8 → P8b → P9 → P10 → P11 → P12**

### P6 — Responsive Theme and Multilingual Infrastructure (for Admins)

```
Read docs/PROJECT_RULES.md and docs/PRD.md section 13. Frontend-only task. NEW files under
src/features/shell/, src/i18n/, src/components/ui/. Allowed minimal edits: src/main.tsx,
index.html, package.json, docs/UI_RULES.md (new). Do not edit src/App.tsx. Do not translate
legacy planner screens.

Step 1 (plan only): file list and dependencies. Wait for "go". Allowed new dependencies:
react-router-dom, i18next, react-i18next. Nothing else.

After "go":
1. App shell (src/features/shell/AppShell.tsx), mobile-first:
   - Below 768px: top bar + bottom navigation (max 5 items). From 1024px: side navigation.
   - Touch targets >= 44px, no hover-only actions, safe-area insets
     (env(safe-area-inset-*)), index.html viewport meta with viewport-fit=cover.
   - Routes: "/" Home (honest placeholder listing implemented modules only),
     "/legacy" the existing App wrapped in a banner "Demo route planner — not operational
     data". Login stays as is. Nav items appear only for pages that exist.
2. i18n (src/i18n/): locales fa, ar, ckb (Kurdish Sorani), en. Namespaces common, auth, shell.
   - Default: saved choice (localStorage, try/catch) -> browser language -> en. Fallback en.
   - Language switcher on the login page AND in the shell. On change set <html lang> and
     <html dir> (rtl for fa/ar/ckb). Use lang "ckb" for Sorani.
   - Every non-English file starts with the comment "NEEDS HUMAN REVIEW".
   - Vitest test: all locales have EXACTLY the same key set as en (fail listing missing keys).
3. RTL rules for NEW code: only logical Tailwind utilities (ms-/me-/ps-/pe-/start-/end-/
   text-start); chevrons/arrows flip with rtl:rotate-180; user-entered text uses dir="auto";
   phone numbers, codes, coordinates and URLs use dir="ltr"; person names wrapped in <bdi>.
   Document these in docs/UI_RULES.md.
4. Formatting helpers (src/i18n/format.ts): formatDate/formatDateTime/formatNumber with Intl,
   timezone Asia/Baghdad, Gregorian calendar, numberingSystem "latn" by default (a per-user
   digit preference comes later). If Intl lacks ckb in this environment fall back to ar-IQ
   and say so. Unit tests for all four locales.
5. Fonts: Google Fonts Vazirmatn for fa, Noto Sans Arabic for ar and ckb, system sans for en,
   each with fallbacks. The Kurdish sample "ڕێبەر ڵ ۆ ێ ە" must render without missing glyphs.
6. Translate ONLY strings that exist in the new UI (login, shell, nav, errors, sign out).
   Move existing login/shell strings to translation keys. Never machine-guess pharmaceutical
   terms; keep those in English until a glossary exists.
7. Verification: npm run lint, npm run build, vitest. You cannot operate the preview: do NOT
describe its appearance; list the exact things I must check by hand on a phone and a
desktop. Follow rule 12 for the report.
```

** Manual check after P 6: ** Change four languages on a real phone; screen orientation, Kurdish font, text not popping out of buttons, page not popping up from keyboard. Human review of translations .

### P4 — RBAC and Tenant Isolation ( Claude review required )

```
Read docs/PROJECT_RULES.md and docs/PRD.md section 3.

Step 1 (plan only): permission list, role-to-permission matrix taken from the PRD, files and
tests. Wait for "go".

- Permissions are independent strings (e.g. customers.read, customers.write, visits.plan,
  visits.assign, visits.execute, tracking.view, audit.view, users.manage, devices.manage,
  reports.view). Map ONLY the PRD roles (super_admin, company_manager, sales_manager,
  supervisor, accounts_manager, sales_rep, medical_rep, warehouse_keeper, driver).
- server/auth/permissions.ts + requirePermission(perm) middleware (403 with no detail).
- A scope helper that ALWAYS adds tenant_id (from req.auth) to queries, and narrows by
  team/region/owner where the PRD says so. Create teams/regions tables only if the PRD needs
  them, in a NEW migration with RLS enabled.
- super_admin is tenant-less for cross-tenant work: a SEPARATE router /api/v1/platform/*
  with its own permission, every call audited. Normal routes never accept a tenant id from
  the request.
- Apply to every existing /api/v1 route. Nothing else in this task.
- Test matrix per endpoint family: allowed, wrong role, other tenant, other team/region,
other owner, inactive user, super_admin on normal routes. Paste the pass/fail table into
docs/evidence/p4.txt; chat gets counts only.
- No UI-only checks count as done.
```

### P4b — User Management (API and Web Page ) ( Claude review required )

```
Read docs/PROJECT_RULES.md and docs/PRD.md sections 3 and 4.

Step 1 (plan only). Wait for "go". Migration 0004 is a NEW file.

- Migration: users.locale text default 'en' CHECK in (fa, ar, ckb, en), users.full_name,
  users.phone (nullable), users.must_change_password boolean default false,
  users.last_login_at, users.team_id/region_id only if P4 created those tables.
- Endpoints /api/v1/users (permission users.manage, tenant scoped): list with search and
  pagination, get, create (username pattern ^[A-Za-z0-9._@-]{3,64}$, role from the PRD list,
  temporary password generated by the SERVER and shown ONCE in the response, must_change_password
  = true), update (role, team, active flag), deactivate (revokes all their sessions at once),
  reset-password (new temporary password, revokes sessions), list sessions of a user,
  kill one session.
- POST /api/v1/auth/change-password (current + new, min 12 chars, revokes other sessions).
  Login with must_change_password=true returns a token that ONLY works for change-password.
- PATCH /api/v1/me/locale.
- Role limits: only super_admin creates company_manager; a manager cannot grant a role above
  their own; no one can deactivate the last active company_manager of a tenant.
- Every action audited in the SAME transaction as the change. Passwords never appear in audit
  or logs.
- Web page src/features/users/ (list, create, edit, deactivate, reset password, sessions) with
  loading/empty/error states, using the P6 shell and translation keys for all four languages.
- Tests: permission matrix, cross-tenant access (404), last-manager rule, forced password
  change, deactivation kills sessions, reset revokes sessions, audit rows exist.
```

### P5 — Audit Log (فقط‌خواندنی) (بازبینی Claude لازم)

```
Read docs/PROJECT_RULES.md and docs/PRD.md sections 2 and 12.

Step 1 (plan only). Wait for "go".

- Central audit(client, event) service that always runs inside the caller's transaction for
  state-changing actions (failure => the action fails). Refactor existing callers (auth,
  users) to use it.
- Append-only: NEW migration with a trigger that rejects UPDATE, DELETE and TRUNCATE on
  audit_logs, and revokes those privileges from the application role where possible. State
  plainly that a database superuser can still bypass this.
- GET /api/v1/audit with filters (date range, actor, entity type, action), cursor pagination,
  permission audit.view, tenant scoped, sensitive fields redacted. No update/delete API.
- Web page src/features/audit/ (read-only, loading/empty/error states, 4 languages).
- Tests: no delete/update via API, trigger blocks direct UPDATE/DELETE, other tenant sees
  none, redaction, pagination stability.
```

### P7 — ماژول مشتریان

```
Read docs/PROJECT_RULES.md and docs/PRD.md section 8.

Step 1 (plan only). Wait for "go".

- Migration (NEW file, RLS enabled): customers (tenant_id, type, code unique per tenant, name,
  phone, address, city, region, geom geography(Point,4326) nullable, location_source,
  location_updated_at, location_updated_by, status, owner_user_id, created_at) + GIST index.
  Customer without GPS has status "no_location".
- Endpoints /api/v1/customers: list (search, filter, cursor pagination), get, create, update.
  Any location change is audited with old/new coordinates summary and who changed it.
  Reps see only their own or their team's customers per the PRD.
- Web UI src/features/customers/: list, detail, create/edit form, map picker reusing the
  existing Google Maps component WITHOUT editing it. Loading/empty/error states, 4 languages.
  NO sample customers; an empty database shows the empty state.
- Do not touch the legacy "Add Pet" action.
- Tests: the 5 access cases, no_location status, audit on location change, code uniqueness.
```

### P8 — ویزیت: برنامه‌ریزی، تخصیص و ماشین وضعیت

```
Read docs/PROJECT_RULES.md and docs/PRD.md section 5.

Step 1 (plan only). Wait for "go".

- Migration (NEW, RLS enabled): visits (tenant_id, customer_id, type, assignee_id,
  planned_for date, priority, status, unplanned_reason, created_by, created_at) with status in
  planned, en_route, arrived, in_visit, completed, failed, cancelled; visit_events table
  (visit_id, from_status, to_status, actor, at, note) as the transition history.
- Server-side state machine: planned -> en_route -> arrived -> in_visit -> completed.
  failed/cancelled require a reason and a permitted role. No client can jump states.
- Idempotency-Key header on transitions (stored per user); a repeated request returns the
  first result and never double-applies.
- GET /api/v1/me/visits?date=YYYY-MM-DD computed in Asia/Baghdad; no visits = 200 [].
- Manager UI src/features/visits/: create, assign, reassign (audited), day board.
  Honest empty states, 4 languages.
- Tests: illegal jump 409, other user's visit 404, other tenant 404, duplicate request,
  reassignment audit, Baghdad day boundary (a visit at 23:30 UTC belongs to next day).
```

### P8b — دستگاه‌ها و Device Binding (API) (بازبینی Claude لازم)

```
Read docs/PROJECT_RULES.md and docs/PRD.md sections 4 and 11.

Step 1 (plan only). Wait for "go".

Build on the existing devices table; changes go into a NEW migration.
- devices gets: platform (android|ios), app_version, os_version, install_id, secret_hash,
  approved_by, approved_at, last_seen_at, blocked_reason. status: pending|approved|blocked.
- POST /api/v1/devices/register {deviceLabel, platform, installId, appVersion} for a user
  who just logged in: creates a device in status pending and returns deviceId + a random
  deviceSecret ONCE (only its hash is stored).
- Roles that require binding (config, default: sales_rep, medical_rep, driver, warehouse_keeper):
  login must include deviceId + deviceSecret; otherwise 403 DEVICE_NOT_APPROVED with the
  device status (pending/blocked/unknown) and no other detail. Managers on the web do not
  need binding.
- Admin endpoints (permission devices.manage, tenant scoped): list, approve, block (revokes all
  sessions of that device immediately), unbind. Every action audited in the same transaction.
- A session is tied to its device; blocking the device kills the session on the next request.
- Rate limiting for deviceSecret failures as for passwords; constant-time comparison.
- Web page src/features/devices/ (list, approve, block) with 4 languages.
- Tests: pending device cannot log in, approved can, blocked is rejected, secret mismatch, block
  revokes sessions immediately, cross-tenant access, audit rows.
- State plainly: the end-to-end flow is NOT verified until the mobile app exists.
```

### P9 — Check-in / Check-out و گزارش ویزیت (API) (بازبینی Claude لازم)

```
Read docs/PROJECT_RULES.md and docs/PRD.md sections 5.3 and 6.

Step 1 (plan only). Wait for "go".

Defaults until the product owner decides (all config):
- Check-in radius 20 m (per-customer override); distance computed on the SERVER with PostGIS
  ST_DWithin on geography. Check-out needs a valid position but not necessarily inside the radius.
- Payload: lat, lng, accuracy_m, captured_at, is_mock (bool from the client), provider, client_event_id.
  Reject: invalid coordinates, captured_at older than 5 minutes or in the future, accuracy worse
  than 100 m (config). If is_mock is true: reject and audit.
- Visit report: result (versioned enum) + meaningful note (not empty/whitespace). Failing to
  save the report must never complete the check-out.
- Every accepted event stores the server-computed distance and a fraud_flags array.
- Idempotent by client_event_id. No partial state on failure.
- Document all endpoints in docs/openapi.yaml (create it now; the mobile team depends on it).
- Tests with direct API attempts: bad coordinates, stale timestamp, wrong owner, wrong state,
outside radius, mock flag, duplicate event, concurrent duplicate requests.
- This task has NO mobile UI. State that the device flow is NOT verified.
```

### P10 — Shift, receive tracking and live map ( Claude review required )

```
Read docs/PROJECT_RULES.md and docs/PRD.md section 6.2.

Step 1 (plan only). Wait for "go".

- Migration (NEW, RLS enabled): shifts (user_id, started_at, ended_at, device_id),
  tracking_points (tenant_id, user_id, shift_id, geom, accuracy_m, speed_mps, heading, altitude,
  captured_at, received_at, provider, is_mock, battery_pct, app_state, client_point_id,
  fraud_flags text[]) with a unique (user_id, client_point_id), BRIN/btree on captured_at, and
  a GIST index; tracking_status_events (user_id, kind: shift_started|paused|resumed|stopped|
  permission_revoked|battery_saver|gps_off|app_killed_suspected, at, note).
- POST /api/v1/shifts/start and /shifts/end (idempotent). Tracking is accepted only during an
  open shift (default; product owner to confirm).
- POST /api/v1/tracking/points: batch up to 200 points, idempotent per client_point_id.
  Server rules per point: reject future timestamps; flag is_mock, accuracy > 100 m, an
  implied speed above 200 km/h versus the previous point, and time going backwards. Store
  the flags; never silently drop flagged points.
- POST /api/v1/tracking/status-events.
- GET /api/v1/tracking/live (permission tracking.view, scoped to the caller's team/region):
  per user: last point, age, and a state: live (age <= 2x configured interval), stale,
  paused, off_shift, permission_problem. Never label stale data as live.
- GET /api/v1/tracking/route?userId&date returns the ordered points and the distance summed on
  the server, plus counts of flagged points and detected gaps (> 5 minutes).
- Retention: config TRACKING_RETENTION_DAYS (default 90) + a cleanup function (no cron).
  Viewing someone's live/route is itself audited.
- Web page src/features/tracking/ reusing the existing Google Maps component WITHOUT editing
  it (wrap it): live list + map, route replay, gap markers. Honest empty state; no simulated
  movement. 4 languages.
- Tests: batch idempotency, flags, state labels, tenant/team isolation, permission, audit of
  viewing, retention cleanup, distance calculation on a known path.
```

### P11 — پنل عملیاتی و گزارش‌های پایه

```
Read docs/PROJECT_RULES.md and docs/PRD.md section 10.

Step 1 (plan only). Wait for "go".

- KPI endpoint(s) using only tables that exist: planned/completed/failed visits, delays,
  new customers, overdue follow-ups if the table exists, tracked distance and gaps per user.
- Document each KPI's numerator, denominator and time window in docs/kpi-definitions.md.
- Filters: date range, team/region, user, customer type; permission-scoped.
- UI src/features/dashboard/: cards + table + a simple chart; every card has loading/empty/error
  states; an empty database reads 0/empty, never sample numbers. 4 languages.
- Tests: KPI values against a small controlled dataset created inside the test.
```

### P12 — تحویل مستند برای اپ موبایل

```
Read docs/PROJECT_RULES.md and docs/PRD.md sections 11 and 15.

- Complete docs/openapi.yaml from the REAL implemented routes only (auth, devices, me/visits,
  visit transitions, check-in/out, reports, shifts, tracking, customers read, locale). Validate
  it with a linter (add as a devDependency and an npm script). Include examples and every
  error code.
- docs/mobile-contract.md: login + device registration flow, token handling, idempotency keys,
  batching limits, time rules (UTC on the wire), retry/backoff rules, offline queue rules, and the
  list of error codes the app must handle.
- docs/test-report.md and docs/known-gaps.md from a real run.
```

---

## 9. Mobile app ( Android and iPhone)

### 9.1 Things you should know

- ** Native or cross-platform? ** With two operating systems, cross-platform is a big savings. Initial suggestion : **React Native with Expo (Development Build , not Expo Go)** , since the web is both React and TypeScript and the API client is generated from ` openapi.yaml ` . Flutter is also a serious option . ** Initial testing (M0) will make the decision ** ; if background tracking with the lightweight tool is unstable, a commercial background tracking plugin ( e.g. Check out Transistorsoft and see its cost and license on its website .
- ** Android: ** Foreground service with persistent notification; separate background location permission; from Android 14 `location` service type must be declared. Manufacturers like Xiaomi , Huawei , Oppo/Realme , Samsung and some others may kill the app after a few minutes with `battery optimization`; each brand's in-app settings guide and optimization exemption request required. Admin panel should show `Tracking stopped` .
- ** iPhone: ** Location permission "Always" and Background Modes/Location capability . As far as I know, if the user closes the app from the recent apps list (Force Quit) , tracking stops until the app is reopened; this should be included in user training and in the admin panel status. The blue bar indicating location usage is always visible .
- ** Release :* * Android: Internal Testing or direct APK distribution to 50-150 people . iPhone: Apple Developer account required and installation outside of App Store/TestFlight/ Enterprise distribution not possible; plan for Apple review time and cost now. iOS development without Mac possible with cloud service ( like EAS Build) .
- ** RTL: ** In React Native , changing the right-to-left orientation usually requires an app restart; ask the user for the language on first run or show a restart after the change .
- ** Building tools :* * A development environment is required (Node , Android SDK , and for iOS or Mac or cloud build ) and a coding agent on your computer . AI Studio is not suitable for this part .

### 9.2 M0 — Background tracking test (first of all, no backend)

```
Build a minimal test app "LifeDrop TrackTest" (React Native + Expo development build,
TypeScript). It has ONE screen and NO backend. Goal: measure whether background location
tracking survives screen-off on real phones.

Features:
1. Buttons: Start tracking, Stop tracking, Export log, plus live counters (points recorded, last
   point time, current app state).
2. Permissions flow: foreground then background location (iOS "Always"; Android background
   permission + notification permission). Show the current permission state and what is missing.
3. Tracking: background location updates every 15 seconds (distanceInterval 0), highest useful
   accuracy, Android foreground service with a persistent notification, iOS background location
   with the blue indicator. Use expo-location + expo-task-manager first.
4. Each point is appended to a local file: ISO time, lat, lng, accuracy, speed, heading, provider
   or mocked flag (Android), battery level, battery saver flag, app state, and a monotonic counter.
5. Also log lifecycle events: app foregrounded/backgrounded, task started/stopped, permission
   changes, low power mode changes.
6. Export: share the CSV through the OS share sheet.
7. A separate script (scripts/analyze.ts) reads the CSV and prints: expected vs recorded points,
   longest gap, number of gaps > 2 and > 5 minutes, first-death time if tracking stopped.
8. Android: a screen with a button that requests the battery-optimization exemption and shows
   brand-specific guidance links; log the device manufacturer/model/OS version at start.

Deliver: source, build instructions for Android (APK) and iOS (TestFlight or local), and a
README with the exact test protocol below. Do not add features.
```

** Test protocol ( on any Android brand and model and on iPhone)* *
1. Screen off, phone in pocket, 2 hours of walking or driving .
2. 2 A standing clock on a table with the screen off .
3. Close the app from the recent list (Swipe away) and wait 30 minutes .
4. With and without battery saving mode .
Result: For each phone, a table of “Expected/Recorded Points, Longest Distance, Stop Time ”. ** Acceptance criteria are determined by the product owner ** ( Suggested: at least 90% of points and a maximum distance of 5 minutes when moving ).

### 9.3 Next steps of the app ( their prompt is written after the result of M0 )

| Stage | Content | Backend Prerequisite |
|---|---|---|
| M1 | Skeleton: Login, device registration and storing ` deviceSecret ` in Keychain/Keystore , language selection (fa , ar , ckb , en ) | P3 , P8b , P12 |
| M2 | Today's visit list, status change, Check-in/Check-out with GPS , visit report | P8 , P9 |
| M3 | Offline queue with Idempotency key , backlog synchronization, behavior when internet is down | P8 , P9 |
| M4 | Full shift and tracking: point classification, dispatch, status events, recovery after stop | P10 |
| M5 | Hardening: Brand Guide, Troubleshooting and Error Reporting, Forced Version Update, User Training, Release | All |

---

## 10. Open decisions

| Topic | Status |
|---|---|
| Tracking with the screen off | ** Decided :* * Mandatory |
| Mobile Platform | ** Decided : * * Android and iPhone |
| Start tracking | Default: At the start of the shift. Requires confirmation |
| Check-out location rule | Default : Valid GPS required, presence within radius optional |
| Check-in radius | Default 20 meters |
| Traceability Acceptance Criteria (M0) | Product Owner Decision Required |
| GPS track retention period and privacy | Default 90 days; requires employee decision and consent |
| Kurdish language Sorani ( ckb ) ; Need to confirm |
| Accounts and Tracking Manager | Access is maintained; level of detail needs to be determined |
| Failed/Cancelled | Product decision: either outcome is allowed from planned, en_route, arrived, or in_visit, with a meaningful reason and server-authorized role; completed, failed, and cancelled are terminal. |
| Maps | Currently Google Maps ; OSM/ MapLibre Only if cost or coverage becomes an issue |
| iOS Build and Release | Apple Developer Account Needed and Release Process; Responsible and Scheduled |
| Who builds the mobile app and with what tools | Need to determine |
| Backup/Restore and RPO/RTO | Responsible: Technical and Database Service Team |
