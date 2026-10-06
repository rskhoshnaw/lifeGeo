# Code map (use instead of scanning the repo)

## Backend (.NET 10, raw SQL with Npgsql, PostgreSQL 16 + PostGIS)
- `src/LifeDrop.Domain/Visits/` — `Visit` state machine, `VisitReport`, `LocationCheck.cs` (GpsFix, LocationCheckPolicy, OfflineCheckPolicy).
- `src/LifeDrop.Application/` — use cases by area: `Visits/VisitOperations.cs` (create, en-route, arrive, fail, manage, reports, `VisitCheckpoint` check-in/out), `Visits/OfflineReviews.cs`, `Customers/CustomerOperations.cs` (create + `CustomerApprovals`), `Tracking/ShiftTracking.cs`, `Tracking/LiveMap.cs` (`/live-map`, D3), `Roles/` (`IPermissionStore`, `PermissionKeys`, data scope tenant/region/own), `Auth/`, `Users/UserDirectory.cs` (panel reads: me, users, roles, devices), `Visits/VisitOperations.cs` `VisitPlanBoard` (`/visit-plan`), `Regions/RegionTeamOperations.cs`, `Visits/PlanOperations.cs` (`PlanVisits`: reorder + postpone, step 1.23), `Visits/PlanGeneration.cs` (`PlanGenerator`, step 1.24; store `Infrastructure/Visits/PostgresPlanGenerationStore.cs`), `Customers/CustomerOperations.cs` `CustomerVisitWindows` + `CustomerProfileService`, `Places/PlaceOperations.cs` (`PlaceService`, `PlaceExport`: places tree, import, merge, export, step 1.23-b), `Payments/PaymentOperations.cs` (`PaymentService`), `Samples/SampleOperations.cs` (`SampleService`), `Visits/VisitReportDetails.cs` (specialised visit form), `FollowUps/FollowUpOperations.cs` (`FollowUpService`), `Orders/OrderOperations.cs` (`OrderService`, warehouses, orders and warehouse work; steps 2.2-2.4), `Catalog/CatalogOperations.cs` (`CatalogService`: products, price rules, effective prices; step 2.1), `Notifications/NotificationOperations.cs` (own messages; created by `PostgresCustomerStore.NotifyAsync`, step 1.23-c).
- `src/LifeDrop.Infrastructure/` — Postgres stores (`Visits/PostgresVisitStore.cs`, `Customers/…`, `Tracking/PostgresShiftStore.cs`). Every write sets `app.current_tenant` (RLS) and writes its audit row in the same transaction; visit writes also set `app.current_actor` (status-history trigger).
- `src/LifeDrop.API/Program.cs` — DI, auth/JWT, CORS, older routes; `Endpoints/*.cs` — newer routes; `ApiErrors.cs` — ProblemDetails codes.
- `db/migrations/00NN_*.sql` (never edit merged ones), `db/grants/lifedrop_app_grants.sql`.
- Contract: `docs/api/openapi-v1.json`; route/permission matrix: `docs/phase0-api-access-matrix.md`.

## Tests
- `tests/LifeDrop.Database.Tests/CustomerApiIntegrationTests.cs` — API + DB tests (large file: grep for the test name; helpers `SetupIsolatedSchemaAsync`, `GenerateToken`, `AssertProblemAsync`, `FakeSessionStore`, `FixedClock`). `DeploymentRehearsalTests.cs` — restricted DB role.
- `tests/LifeDrop.{Domain,Application,Mobile.Auth,Mobile.Visits}.Tests` — unit tests (`LifeDrop.CI.slnf`).

## Mobile (.NET MAUI, Android)
- `src/LifeDrop.Mobile.Auth` — login/session. `src/LifeDrop.Mobile.Visits` — testable logic: `VisitsApiClient`, `DailyPlan`, `VisitLifecycle`, `Tracking/` (ShiftTracker, PointQueue, ShiftApiClient), `Offline/` (Outbox, FileDurableStore), `Customers/` (CustomersApiClient, PlacePicker, NewCustomerFlow: step 1.23-b), `SelfPlan.cs` (SelfPlanner: step 1.25), `Orders/` (OrdersApiClient, OrderDraft, WarehouseApiClient, PreparationDraft, DeliveryApiClient, DeliveryDraft: steps 2.2-2.4).
- `src/LifeDrop.Mobile` — MAUI pages (`HomePage`, `DailyPlanPage`, `AddToPlanPage`, `OrderPage`, `WarehouseOrdersPage`, `WarehouseOrderPage`, `DeliveriesPage`, `DeliveryPage`, `PaymentPage`, `MyPaymentsPage`, `SamplePage`, `VisitFormPage`, `FollowUpPage`, `MyFollowUpsPage`, `VisitDetailsPage`, `CustomerPage`, `NewCustomerPage`, view `PlacePickerView`), `TrackingHost.cs` (shared services), `AppLanguage.cs` (language choice, RTL, root pages), `Platforms/Android/TrackingService.cs`, `AndroidMonotonicClock.cs`.

## Texts (step 1.21)
- `i18n/strings.json` — every user-facing text, key → {en, ckb, ar, fa}; read by the panel (`src/LifeDrop.Web/src/i18n.ts`) and the app (`src/LifeDrop.Mobile.Visits/Localization/Texts.cs`, embedded).

## Web panel
- `src/LifeDrop.Web` — Vite + React + TS strict + Tailwind v4; `src/api/client.ts` (auth), `src/api/admin.ts` (management calls, `can()`), `src/pages/` (Users, Devices, Customers, Visits, Regions, Places, LiveMap, Audit), `components/` (PlanFromPlaces, PlaceCascade, VisitWindowsEditor, CustomerProfileEditor, csv.ts), `src/components/LiveMapView.tsx` (MapLibre), tests `*.test.ts(x)`.

## CI
- `.github/workflows/build.yml` (jobs: build, mobile-android, web), `database-tests.yml` (`MIN_EXPECTED_TESTS`).

## Local (Windows)
- `docker-compose.yml` + `db/local/init.sql` (databases and roles as CI), `db/local/seed_dev.sql` (tenant `dev`, five users, regions, customers), `scripts/local/*.ps1` (Reset-LocalDb, Start-Api, Start-Panel, Run-Android, Test-All); guide `docs/LOCAL-SETUP.md`.
