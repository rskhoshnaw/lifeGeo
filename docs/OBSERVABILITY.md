# Observability and performance (step 3.7)

## Health
- `GET /health/live`: the process answers. `GET /health/ready`: the database answers `SELECT 1` within 2 s and the API's database role is the restricted one. 503 with `database: down | insecure_role` otherwise. A background job that is **stale** (last success older than three intervals, or it has only ever failed) makes the status `degraded` (still 200); the body lists every job.
- Both are open (no data about customers), not rate limited, and not in the OpenAPI contract. Use them for the load balancer (`ready`) and the restart policy (`live`).

## Metrics
- `GET /metrics` (Prometheus text) exists only when `Metrics__Token` is set; send `Authorization: Bearer <token>`. Without the token the route is 404, with a wrong one 401.
- `lifedrop_http_requests_total{route,method,status}` and `lifedrop_http_request_duration_seconds` (histogram, buckets 0.05 to 10 s). The label is the **route template** (`/orders/{orderId:guid}`), never the path, so ids and customer data never enter the metrics.
- `lifedrop_job_runs_total`, `lifedrop_job_failures_total`, `lifedrop_job_last_success_timestamp_seconds` per job: `alert_scan`, `push_dispatch` (only once a Firebase key exists), `report_schedule`.
- Counters live in memory of one API process and restart at zero; Prometheus handles that with `rate()`.

## Request log
One line per request (not for health and metrics): method, route template, status, milliseconds, tenant id, trace id (the same `traceId` as in error responses). Level Warning for status 500 and above or 2 s and slower. No query, body or path values are logged.

## Alert rules (examples for Prometheus)
```yaml
- alert: ApiErrors
  expr: sum(rate(lifedrop_http_requests_total{status=~"5.."}[5m])) / sum(rate(lifedrop_http_requests_total[5m])) > 0.02
  for: 10m
- alert: ApiSlow
  expr: histogram_quantile(0.95, sum by (le) (rate(lifedrop_http_request_duration_seconds_bucket[5m]))) > 1
  for: 10m
- alert: JobStopped
  expr: time() - lifedrop_job_last_success_timestamp_seconds > 900
  for: 5m
- alert: JobFailing
  expr: increase(lifedrop_job_failures_total[30m]) > 3
- alert: NotReady
  expr: probe_success{job="lifedrop-ready"} == 0
  for: 2m
```
(`900` s is three alert-scan intervals; change it per job.)

## Load test
`node scripts/perf/load.mjs --url https://api-test.example --path /health/ready --requests 2000 --concurrency 20 [--token <bearer>] [--max-p95-ms 500] [--max-error-rate 0.01]` prints status counts, requests per second and p50/p95/p99, and exits 1 when a limit is broken. Run it against a **test copy** of the API with test data; use read routes (`/live-map`, `/reports/kpis`, `/orders`) with a test user's token. The API limits each address to 100 requests a minute, so a load test from one address needs the limit raised on the test copy or several addresses.

Targets are not decided yet (owner): suggested p95 under 500 ms for read routes and 1 s for writes at the expected number of field users, error rate under 1 %.
