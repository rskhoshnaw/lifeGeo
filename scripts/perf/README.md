# Load test of a field day (plan step 5.70)

Everything here runs against a **throw-away copy**: a database made by `deploy/setup-db.sh` and filled by `seed-load.sql`, and an API started on it. Never point it at real data.

```bash
# 1. database + schema (the real migrations), then the data: 150 visitors with 12 customers and 12 planned visits each, 3 managers, a warehouse for the city, 40 products
export PGHOST=localhost PGUSER=postgres PGPASSWORD=...   LIFEDROP_DB_NAME=lifedrop_load APP_DB_PASSWORD=load-pass PSQL_CMD="psql -v ON_ERROR_STOP=1 -q -t -A"
deploy/setup-db.sh
psql -d lifedrop_load -v visitors=150 -f scripts/perf/seed-load.sql

# 2. the API, as a release build, with the rate limit lifted (the real limit is per address: 100 requests a minute)
dotnet publish src/LifeDrop.API -c Release -o /tmp/api-load
cd /tmp/api-load && ConnectionStrings__LifeDrop="Host=localhost;Database=lifedrop_load;Username=lifedrop_app;Password=load-pass" Jwt__Issuer=load Jwt__Audience=load \
  Jwt__SigningKey="load-test-signing-key-at-least-32-bytes-long-000" RateLimit__PerMinute=10000 ASPNETCORE_URLS=http://127.0.0.1:5080 ASPNETCORE_ENVIRONMENT=Production dotnet LifeDrop.API.dll

# 3. the field day: 50 visitors for 5 minutes at real-life speed, then 6 times and 12 times as busy
node scripts/perf/scenario.mjs --visitors 50 --minutes 5 --speed 1 --pid <API process id>
node scripts/perf/scenario.mjs --visitors 50 --minutes 3 --speed 6 --pid <API process id>
node scripts/perf/scenario.mjs --visitors 150 --minutes 3 --speed 12 --pid <API process id>
```

`seed-load.sql` makes the sessions and phones itself, and `scenario.mjs` signs the access tokens with the same key, so the run measures the API and the database, not the password check. A run needs a fresh seed
(visits are finished once); `--max-p95-ms` and `--max-error-rate` make the exit code 1 when a limit is broken. `load.mjs` is the older single-route hammer.

What a visitor does: starts a shift; every minute sends two positions with the phone's health and asks for the unread count; reads the day's visits; about every 3 minutes goes through a visit (on the way, arrive, start,
report, complete at the customer's place) and, in half the cases, places an order without choosing a warehouse (the server finds the warehouse of the customer's city). Three managers read the live map, the plan, the orders and the KPIs.

## Results (2026-11-28, one machine: 4 cores, 16 GB, API and PostgreSQL 16 + PostGIS on it, release build)

| Run | Requests/s | Errors | Worst route p95 | API CPU (avg / max, of one core) | API memory |
| --- | --- | --- | --- | --- | --- |
| 50 visitors, real speed, 5 min | 3.2 | 0 | 66 ms (KPIs) | 7 % / 27 % | 202 MB |
| 50 visitors, 6 times, 3 min (like 300 visitors) | 21.5 | 0 | 67 ms (KPIs) | 17 % / 38 % | 204 MB |
| 150 visitors, 12 times, 3 min (like 1800 visitors) | 126 | 0 | 76 ms (live map); 613 ms for the first 150 shift starts right after the API started | 39 % / 58 % | 218 MB |
| the same on a database with 4.8 million tracking points (100 days of 50 visitors, 2.3 GB) | 120 | 0 | 65 ms (orders); points p99 194 ms | 19 % / 20 % | 227 MB |

Single reads on that big database (median of 5): live map 7 ms, route history of a day 9-14 ms (any of the last 90 days), KPIs for 1, 30 and 90 days 15-16 ms, orders 4 ms, the manager's plan of 1800 visits 15 ms but **1 MB** of answer.

## What it showed
- **Nothing slow or broken at the expected size.** 50 visitors at real speed is 3 requests a second. Even 36 times that stayed under 80 ms at the 95th percentile with no error.
- **The first seconds after a restart are slow** (cold code and an empty connection pool): 150 shift starts in 5 seconds right after the API started had p95 613 ms; the same burst on the warm API had p95 7 ms. After `update.sh` the first requests may take up to a second.
- **Orders are the heaviest write** (about 40 ms, up to 400 ms in the worst second): they lock the stock rows of the products in the warehouse, so many orders for the same products queue. Not a problem at this size.
- **`GET /visit-plan` for a manager is not paged**: all visits of the day come in one answer (1 MB for 1800 visits, about 300 KB for 600). Fine for one company of this size; it grows with the number of visits.
- **Disk:** a tracking point costs about 490 bytes with its indexes. 50 visitors send about 48,000 a day (23 MB), so the 90 days that are kept (the history reads stop at 90 days) are about 2 GB; 150 visitors about 6 GB.
- The default **rate limit is per address: 100 requests a minute**. A visitor uses about 3 a minute, so phones are far below it; an office where many panels share one address is the place to watch (`RateLimit:PerMinute`, up to 10000).
