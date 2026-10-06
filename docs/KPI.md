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

Not in this step: payments and samples (they can be added to the same report later).
