# Who can open what (default roles, plan step 4.34)

Checked by the test `RoleMatrix_EveryDefaultRoleOpensOnlyWhatItShould`: one user per default role calls each list endpoint of the panel. ✓ = allowed (200), empty = refused (403).
What a role sees *inside* an allowed list depends on its data scope: CompanyManager, SalesManager, FinanceManager = whole company; Supervisor and SalesRep = their region; ScientificRep, WarehouseManager, Driver = own records (warehouse: only the warehouses they work in; driver: only orders handed to them).

| Path | CEO | SalesMgr | Superv | Finance | SalesRep | SciRep | Whse | Driver |
|---|---|---|---|---|---|---|---|---|
| `/alerts` | ✓ | ✓ | ✓ |  |  |  |  |  |
| `/audit-logs` | ✓ | ✓ |  | ✓ |  |  |  |  |
| `/channels` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/customer-approvals` |  | ✓ | ✓ |  |  |  |  |  |
| `/customers` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |
| `/customers/map` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |
| `/deliveries` |  |  |  |  |  |  |  | ✓ |
| `/devices` |  | ✓ |  |  |  |  |  |  |
| `/followups` | ✓ | ✓ | ✓ |  | ✓ | ✓ |  |  |
| `/form-fields` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/live-map` |  | ✓ | ✓ | ✓ |  |  |  |  |
| `/notifications` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/offline-reviews` |  | ✓ | ✓ |  |  |  |  |  |
| `/orders` | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |  |
| `/payments` | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |  |
| `/payments/mine` |  |  |  | ✓ | ✓ |  |  |  |
| `/places` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |
| `/platform/tenants` |  |  |  |  |  |  |  |  |
| `/products` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |
| `/regions` | ✓ | ✓ | ✓ |  |  |  |  |  |
| `/reports/kpis` | ✓ | ✓ | ✓ |  |  |  |  |  |
| `/roles` |  | ✓ |  |  |  |  |  |  |
| `/samples` | ✓ | ✓ | ✓ |  | ✓ | ✓ |  |  |
| `/saved-reports` | ✓ | ✓ | ✓ |  |  |  |  |  |
| `/settings/shift-alerts` | ✓ | ✓ | ✓ |  |  |  |  |  |
| `/teams` | ✓ | ✓ | ✓ |  |  |  |  |  |
| `/tracking/users` |  | ✓ | ✓ |  |  |  |  |  |
| `/users` |  | ✓ | ✓ |  |  |  |  |  |
| `/visit-plan` |  | ✓ | ✓ |  |  |  |  |  |
| `/visits` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `/warehouse/drivers` |  |  |  |  |  |  | ✓ |  |
| `/warehouse/orders` |  |  |  |  |  |  | ✓ |  |
| `/warehouses` | ✓ | ✓ | ✓ | ✓ | ✓ |  |  |  |

`/channels`, `/notifications` and `/form-fields` are open to everybody on purpose (own message channel, own messages, field definitions the forms need). `/visits` lists only the caller's own visits for roles without `visits.write`.
