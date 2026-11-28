# KPI definitions (plan step 2.12, PRD §10)

Every KPI is **numerator ÷ denominator**, both counted from the real data when the report is asked for (`GET /reports/kpis`). A rate is shown with one decimal; **when the denominator is 0 there is no value (a dash), never 0 %**.
A total adds the numerators and the denominators of the users (it is not an average of rates). Money is summed per currency, never across currencies.
Days are **Iraq days** (Asia/Baghdad). Filters: day range (from and to, at most 366 days), region, team (the team of the user), customer type. A reader sees only the users inside their area
(`reports.read`: CompanyManager and SalesManager all; Supervisor their region). A user with no data in the period is not listed.

| KPI | Numerator | Denominator | Counted for / by |
|---|---|---|---|
| Visits planned | — | — | Visits scheduled (`schedule_date`) in the range whose status is not Cancelled, by the assigned user; customer type filter on the visit's customer |
| Visit completion rate | Visits Completed | Visits planned | same |
| Visit failure rate | Visits Failed | Visits planned | same |
| Late-start rate (delay) | Visits checked in later than the planned time + 30 minutes, or never checked in although that window is over | Visits with a planned time (not cancelled) that were started or whose window (planned time + 30 minutes, now in Iraq time) is already over. A visit that is not due yet is in neither | same; first check-in time = the first `InVisit` in the visit's history |
| New customers | Customers created in the range (Iraq day), by their owner | — | with "of which approved" (`approval_status = approved`) |
| Orders | Orders created in the range that are not cancelled, by the user who placed them; with the total per currency | — | customer type filter on the order's customer |
| Order delivery rate | Orders Delivered | Orders Delivered + Orders Delivery-failed (orders still on the way are in neither) | same orders |
| Follow-ups overdue rate | Follow-ups still open (pending or in progress) whose due day is before today | Follow-ups due in the range that are not cancelled | by the responsible user |
| Alerts | Alerts of the users that occurred in the range, by kind, with how many were handled and the result of the review (valid / suspicious / fraud) | — | only the categories the reader may read (security needs `alerts.security.read`) |

Added in step 5.73 (same filters; counted by the day the record was created, Iraq days; each is a count or a money total, so there is no rate):

| Figure | What is counted | By whom |
|---|---|---|
| Samples given | Sample distributions in the range whose status is `given` (a voided one is not counted) | the user who gave them (`distributed_by`) |
| Money received, confirmed | Payments the customer paid (`direction` in; a refund is not money received) with status `confirmed`, the sum per currency | the user who received the money (`received_by`) |
| Money received, waiting | The same with status `collected`: taken by the visitor, not yet confirmed by the finance manager | same |
| Approved returns | Returns with status `approved` (requested, rejected and cancelled ones are not counted), their number and their value per currency | the user who wrote the return |

Voided and replaced payments are not money. A result saved before step 5.73 has none of these figures: they read as 0 / empty and the totals still add up.

## Own numbers and comparison (plan step 5.29)
`GET /reports/kpi-compare` gives one user's numbers for one calendar month (Iraq days, no customer-type filter) next to the month before, the same month a year earlier and 12 months of trend. A visitor reads their own (app: "My KPIs"); a reader with `reports.read` can ask for a user inside their area (panel: Reports, "KPIs: compare months"). A count changes by a percentage of the earlier month (no percentage when it was 0), a rate by percentage points; more is better except for failures, late starts and overdue follow-ups.

## Net sales and sales per warehouse or city (plan step 5.74)
- **Net sales** (a money figure per currency in `counts.netSales`) = the total of the orders of the range (not cancelled) minus the total of the approved returns of the range. Orders count by the day they were placed and returns by the day they were written, so a return of an old order lowers the month it was written in. A currency that has only returns is negative.
- **`GET /reports/sales?from=&to=&by=warehouse|city`** (`reports.read`, the same readers, area and filters as the KPI report: region, team, customer type) lists, per warehouse (the warehouse of the order or the return) or per **city of the customer** (the place of kind city at or above his place): the number of orders, how many were delivered, the order totals, the approved returns (count and value) and the net sales, and a total. Orders and returns that belong to no warehouse or no city come as one row without a name (a customer without a city; a return that put nothing back into a warehouse).
- The app's "My KPIs" shows, for the visitor's own month, the samples, the money received (dinars), the approved returns and the net sales (dinars) next to the month before and last year. Money received and net sales in **dollars** are extra lines (two decimals) shown only when the visitor has dollars in some month of the page (step 5.75).
