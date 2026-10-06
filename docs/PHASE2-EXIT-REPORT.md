# Phase 2 exit report (step 2.15)

Exit criterion (PRD §14): an order from the seller to the delivery, the payment and the finance numbers, with role, region and company limits.

Evidence: `CustomerApiIntegrationTests.Phase2Exit_OrderToDeliveryPaymentAndReportRespectRoleRegionAndCompany` (runs in CI, restricted DB role, RLS on).

| Check | Result expected |
| --- | --- |
| Seller places order, server prices it (18000 IQD) | 201, total from the price list |
| Other company (manager, finance, warehouse) reads, orders with, or confirms anything of it | 404 |
| Driver / finance manager start warehouse work | 403; another company's warehouse user cannot |
| Warehouse prepares (shortage with reason) and hands to the driver; only the assigned driver delivers | sales rep 403 |
| Driver records a payment | 403 |
| Rep collects 6000 IQD, cannot confirm; finance confirms | balance: delivered 14000, confirmed 6000, balance 8000 |
| KPI (sales manager) | 1 order, 1 delivered, 18000 IQD, delivery rate 100.0 |
| KPI (supervisor of another area, finance, other company) | no rows / 403 / no rows |
| Audit | picked_up, on_the_way, delivered each recorded |

Still open before phase 2 is fully closed (owner): real-device tests of orders, warehouse, delivery, payments (`phaseStatus.md`), Firebase project for push (`docs/PUSH.md`), review of translations.
