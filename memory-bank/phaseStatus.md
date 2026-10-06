# Phase status (read this first)

Updated: 2026-10-04. Plan and step table: `docs/IMPLEMENTATION-PLAN.md`. Full history and test evidence:
`memory-bank/archive/` (read only when needed).

| Phase / steps | State |
| --- | --- |
| Phase 0 (0.1–0.10) | ✅ code + CI (`docs/PHASE0-EXIT-REPORT.md`) |
| 1.1–1.9 backend visits | ✅ merged (PRs #5–#13) |
| 1.10–1.14 mobile (plan, visit, check-in/out, shifts + tracking, offline) | ✅ code + CI (PRs #14–#21); **real-device test pending** (`docs/TRACKING-DEVICE-TEST.md`) |
| 1.15 customer registration + approval (D11) | ✅ server (PR #22); mobile form and panel queue later |
| 1.16 web panel skeleton + sign-in + CORS | ✅ merged (PR #23); Cloudflare Pages setup by owner |
| 1.17 panel: users, devices, customers | ✅ merged (PR #24) |
| 1.18 panel: visit planning, regions/teams | ✅ merged (PR #25) |
| 1.19 basic live map (D3 decided: finance = summary) | ✅ merged (PR #26) |
| 1.20 audit viewer in the panel | ✅ merged (PR #27) |
| 1.21 multilingual (ckb default, ar, fa RTL + en) | in PR; translations are Claude drafts, owner reviews `i18n/strings.json` |
| 1.22 end-to-end test | ✅ real phone + local DB: full visit to Completed, audit once per event, deliberate far check-in rejected (`visit.check_in_rejected`), app hides the repeat; direct server repeat is covered by DB tests only |
| 1.23 flexible planning part 1 (order, move to another day, customer visit hours) | in PR; plan section 1-ه lists 1.23-ب … 1.27 |
| 1.23-ب part 1 (places tree, customer profile: server + panel) | ✅ merged (PR #34) |
| 1.23-ب part 2 (app: place pickers with suggestions, customer details, new customer) | in PR; map suggestions moved to 1.27 |
| 1.23-ج reps edit region customers + notifications | in PR |
| 1.24 plan from places (panel) | in PR |
| 1.25 rep plans own day in the app | in PR (same branch as 1.24); real-device check pending |
| 1.26 customer priority from managers | in PR (same branch); migration 0023 |
| 1.27 draw area on map (app, online only) | in PR (same branch); real-device check pending |
| 2.1 catalog and prices | in PR (same branch); migrations 0024, 0025 |
| 2.2 orders and warehouses | in PR (same branch); migration 0026; real-device order test pending |
| 2.3 warehouse work | in PR (same branch); migration 0027; real-device test pending |
| 2.4 delivery by the driver | in PR; migration 0028; real-device test pending |
| Next | **2.5** payments (phase 1 exit also waits for the owner review of translations and the real-device tracking/offline tests) |

Migrations: 0001–0028 (1.24, 1.25, 1.27 have none). Owner must apply 0006–0028 + `db/grants/lifedrop_app_grants.sql` on `lifedrop_dev`.
Open decisions: D4, D6, D8, D9; API host (needed for panel deploy).

## Log (one line per step, newest last)
- 1.15: migration 0018, `/customer-approvals`, `POST /customers/{id}/approval`; DB 33/33.
- 1.16: `src/LifeDrop.Web`, CORS `Cors:AllowedOrigins`, CI job `web`; DB 34/34, panel 14 tests.
- 1.17: `UserEndpoints.cs` (`/me`, `/users`, `/roles`, `/devices`), panel tabs Users/Devices/Customers; no migration; DB 35/35, panel 19 tests.
- 1.18: `GET /visit-plan` (visits.write scope), `/users` also for visits.write; panel tabs Visits and Regions & teams; no migration; DB 36/36, panel 23 tests.
- 1.19: migration 0019 (`tracking.read`/`tracking.summary`, seed + backfill), `GET /live-map`, panel Live map (MapLibre 6 + OSM, lazy chunk); DB 37/37, app 48, panel 27 tests.
- 1.20: panel Audit tab (filters, Iraq-day bounds, cursor paging, read-only), `actorEmail` on `/audit-logs`; no migration; DB 37/37, panel 31 tests.
- 1.21: `i18n/strings.json` (292 keys × ckb/ar/fa/en) used by panel (`src/i18n.ts`, `components/I18n.tsx`) and app (`Mobile.Visits/Localization/Texts.cs`, `Mobile/AppLanguage.cs`); default ckb, RTL; tests check every key in every language + same placeholders + every app key used exists; panel 34, Mobile.Visits 139.
- 1.22 (done, owner on a real phone): panel on port 5273; Android keystore save error on first device registration is now logged (`adb logcat -s LifeDrop`); full visit completed on the phone (`scripts/local/Set-CustomerHere.ps1` moves a customer to the test spot).
- 1.23: migration 0020, `PlanOperations.cs` (`PlanVisits`), `PUT /visit-plan/order`, `POST /visits/{id}/postpone`, `/customers/{id}/visit-windows` (`CustomerVisitWindows`), `windowStatus` in plans; panel Visits/Customers, app plan buttons (online only); DB 39/39, Domain 172, Application 52, Mobile.Visits 162, panel 37 tests.
- 1.23-ب (part 1): migration 0021 (`places` tree + trigger, `customers.place_id`, `places.manage`), `Places/PlaceOperations.cs` (`PlaceService`, `PlaceExport`), `PostgresPlaceStore`, `PlaceEndpoints.cs`, `CustomerProfileService`; panel Places tab (tree, import/export CSV/JSON) + profile editor; no approval step (owner 2026-10-06); DB 41/41, Application 68, panel 46 tests.
- 1.23-ب (part 2, app): `Mobile.Visits/Customers/` (`CustomersApiClient`, `PlacePicker`, `NewCustomerFlow`, `CustomerCodes`), pages `CustomerPage`, `NewCustomerPage`, view `PlacePickerView`, buttons on Home and Visit details; online only; DB 42/42, Mobile.Visits 211 tests; **real-device check pending**.
- 1.23-ج: migration 0022 (`notifications`, SalesRep scope `region`), `Notifications/NotificationOperations.cs`, `PostgresNotificationStore`, `NotificationEndpoints.cs`, `PostgresCustomerStore.NotifyAsync` (same transaction as the change), panel Notifications tab; DB 43/43, panel 49 tests.
- Local setup (not a plan step): Docker DB, seed, PowerShell scripts, `docs/LOCAL-SETUP.md`; checked here: migrations + seed on the compose DB, API as lifedrop_app, register → approve → rep login, DB tests 37/37 against it.
- 1.24: `Visits/PlanGeneration.cs` (`PlanGenerator`), `PostgresPlanGenerationStore`, `POST /visit-plan/preview` + `/generate` (place subtree and/or region, count/order/hours, new visits after the existing order); panel `PlanFromPlaces` + shared `PlaceCascade`; DB 44/44, panel 50 tests.
- 1.25: `/visit-plan/preview|generate` also for `visits.plan` users (self only, `query` text, scope = customers.read); app `SelfPlan.cs` (`SelfPlanner`, `ISelfPlanApiClient` on `VisitsApiClient`), `AddToPlanPage`, button on the plan page; Mobile.Visits 224 tests, DB 44/44.
- 1.26: migration 0023 (`customers.priority`), `Customers/CustomerPriority.cs` + `PostgresCustomerPriorityStore`, `PUT /customers/priority` (ids or place, `visits.write` scope), `customerPriority` on visits and candidates, high-first ordering in plan suggestions; panel `CustomerPriorityBar`; app shows it in plan/add list; DB 45/45, panel 51, Mobile.Visits 226.
- 1.27: `area` filter on `/visit-plan/preview|generate` (PostGIS `ST_Covers`, limits 3-100 points / 0.5 degrees), app `AreaDrawing.cs` (rules, parse, map HTML), `DrawAreaPage` (WebView + Leaflet/OSM from the internet), button on the add-customers page; offline map left to 3.3; JS checked in headless Chromium (tap/undo/clear); DB 46/46, Mobile.Visits 233.
- 2.1: migration 0024 (`products`, `price_rules`, `catalog.read`/`catalog.manage`), `Catalog/CatalogOperations.cs` (`CatalogService`), `PostgresProductStore`, `CatalogEndpoints.cs` (`/products`, `/price-rules/{id}/end`, `/customers/{id}/prices`); one unit + one currency per product; panel Products tab; DB 47/47, panel 53 tests.
- 2.1 (changed 2026-10-07, owner): migration 0025 replaces the region/customer price rules by `city_prices` (per product and city, dated) and `customer_discounts` (percent or amount, one product or all, several stack oldest first); `PriceCalculator`; `/products/{id}/city-prices`, `/customers/{id}/discounts`, `/customers/{id}/prices` shows list price + discounts; panel Products tab; DB 47/47, Application 72, panel 53.
- 2.2: migration 0026 (`warehouses`, `orders`, `order_items`, `order_counters`, orders.* / warehouses.manage, `order_placed` report outcome), `Orders/OrderOperations.cs` (`OrderService`), `PostgresOrderStore`, `OrderEndpoints.cs`; server prices every line (no price in the request), `clientRef` makes retries safe; panel Orders tab; app `OrdersApiClient`, `OrderDraft`, `OrderPage` (buttons on visit details and customer page); DB 48/48, Mobile.Visits 259, panel 55.
- 2.3: migration 0027 (`warehouse_staff`, `order_status_history`, order statuses preparing/ready/handed_over, `driver_id`, prepared quantity + shortage reason, `warehouse.work`; WarehouseManager no longer needs a bound phone), `OrderService` warehouse methods, `/warehouse/orders/*`, `/warehouses/{id}/staff`; panel Warehouse tab + staff in Orders; app `WarehouseApiClient`, `PreparationDraft`, `WarehouseOrdersPage`/`WarehouseOrderPage` (button on Home); seed users warehouse@dev.local and driver@dev.local; DB 49/49, Mobile.Visits 269, panel 57.
- 2.4: migration 0028 (order statuses picked_up/on_the_way/delivered/delivery_failed, receiver, failure reason, delivered quantity + reason, GPS evidence on `order_status_history`, `delivery.work`), `/deliveries/*` (driver only sees assigned orders), `GPS_MOCKED` refused; app `DeliveryApiClient`, `DeliveryDraft`, `DeliveriesPage`/`DeliveryPage` (button on Home); panel shows delivery details; DB 50/50, Mobile.Visits 281, panel 58.
- 2.5: migration 0029 (`payments`, `payment_counters`, `payment_history`, payments.record/manage/read; FinanceManager and SalesRep roles), `Payments/PaymentOperations.cs` (`PaymentService`), `PostgresPaymentStore`, `PaymentEndpoints.cs` (`/payments*`, `/customers/{id}/balance`); visitor payment = collected until the finance manager confirms, void/correct keep history; panel Payments tab; app `PaymentsApiClient`, `PaymentDraft`, `PaymentPage` (button on customer page), `MyPaymentsPage`; DB 51/51, Mobile.Visits 288, panel 60.
- 2.6: migration 0030 (`sample_distributions`, `sample_counters`, samples.record/read/manage; ScientificRep and SalesRep record), `Samples/SampleOperations.cs` (`SampleService`), `PostgresSampleStore`, `SampleEndpoints.cs` (`/samples*`); expired sample refused, fake GPS refused, void with reason; panel Samples tab; app `SamplesApiClient`, `SampleDraft`, `SamplePage` (button on customer page); DB 52/52, Mobile.Visits 296, panel 61.
- 2.7: migration 0031 (`visit_report_details`), `Visits/VisitReportDetails.cs` (`VisitReportDetails`, `IVisitDetailsStore`), `PostgresVisitDetailsStore`, `PUT/GET /visits/{id}/report/details` in `VisitEndpoints.cs`; fields by visit type, another type's fields refused; panel "Visit form" in Visits tab; app `VisitDetailsApiClient`, `VisitDetailsDraft`, `VisitFormPage` (button on visit details); offline later (2.13), photo/signature with file storage; DB 53/53, Mobile.Visits 302, panel 62.
- 2.8: migration 0032 (`follow_ups`, followups.create/manage/read), `FollowUps/FollowUpOperations.cs` (`FollowUpService`), `PostgresFollowUpStore`, `FollowUpEndpoints.cs` (`/followups*`); overdue derived, only the responsible user changes status, cancel with reason, manager assigns/reassigns in area; panel Follow-ups tab; app `FollowUpsApiClient`, `FollowUpDraft`, `FollowUpPage` (customer page), `MyFollowUpsPage` (Home); overdue notification → 2.9; DB 54/54, Mobile.Visits 309, panel 63.
- 2.9: migration 0033 (`alerts`, alerts.read/security.read/handle), `Alerts/AlertOperations.cs` (`AlertService`, `AlertRules`), `PostgresAlertStore` (scan SQL per kind, `ON CONFLICT DO NOTHING` on dedupe key), `AlertEndpoints.cs`, `AlertScanService` (hosted, every 5 min, first after one interval); panel Alerts tab with new-count; DB 55/55, panel 64; more security kinds 2.10, push/SMS 2.14.
- 2.10: migration 0034 (alert kinds impossible_speed, clock_tamper, outside_area, poor_accuracy, device_mismatch; `alerts.evidence`, `handled_outcome`), scan SQL in `PostgresAlertStore`, thresholds in `AlertRules`, `docs/ANTIFRAUD.md`; handling a behaviour alert needs outcome valid/suspicious/fraud; panel shows evidence + outcome select; DB 56/56, panel 65.
