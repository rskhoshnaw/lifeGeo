// Small load test with no dependencies (plan step 3.7): node scripts/perf/load.mjs --url http://localhost:5080 --path /health/ready --requests 2000 --concurrency 20
// Optional: --token <bearer> for authenticated routes, --max-p95-ms 500 and --max-error-rate 0.01 to fail the run (exit code 1) when a limit is broken.
// Prints the status counts, requests per second and p50/p95/p99 latency. Run it against a test copy of the API, never against live customers' data with write routes.
const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]?.startsWith('--') ? 'true' : (all[i + 1] ?? 'true')]] : pairs), []))
const url = new URL(args.path ?? '/health/ready', args.url ?? 'http://localhost:5080')
const total = Number(args.requests ?? 1000)
const concurrency = Math.max(1, Number(args.concurrency ?? 10))
const maxP95 = args['max-p95-ms'] === undefined ? Infinity : Number(args['max-p95-ms'])
const maxErrorRate = args['max-error-rate'] === undefined ? 1 : Number(args['max-error-rate'])
const headers = args.token ? { Authorization: `Bearer ${args.token}` } : {}

const latencies = []
const statuses = new Map()
let started = 0

async function worker() {
  while (started < total) {
    started++
    const begin = performance.now()
    let status = 'network_error'
    try {
      const response = await fetch(url, { headers })
      await response.arrayBuffer()
      status = String(response.status)
    } catch {
      // counted as a network error
    }
    latencies.push(performance.now() - begin)
    statuses.set(status, (statuses.get(status) ?? 0) + 1)
  }
}

export function percentile(sorted, p) {
  if (sorted.length === 0) return 0
  return sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)]
}

const begin = performance.now()
await Promise.all(Array.from({ length: concurrency }, worker))
const seconds = (performance.now() - begin) / 1000
const sorted = [...latencies].sort((a, b) => a - b)
const failures = [...statuses].filter(([s]) => s === 'network_error' || Number(s) >= 500).reduce((n, [, c]) => n + c, 0)
const errorRate = failures / Math.max(1, latencies.length)
const p95 = percentile(sorted, 95)
console.log(JSON.stringify({ url: url.href, requests: latencies.length, concurrency, seconds: Number(seconds.toFixed(2)), requestsPerSecond: Number((latencies.length / seconds).toFixed(1)), statuses: Object.fromEntries(statuses),
  latencyMs: { p50: Number(percentile(sorted, 50).toFixed(1)), p95: Number(p95.toFixed(1)), p99: Number(percentile(sorted, 99).toFixed(1)), max: Number((sorted.at(-1) ?? 0).toFixed(1)) }, errorRate: Number(errorRate.toFixed(4)) }, null, 2))
if (p95 > maxP95 || errorRate > maxErrorRate) {
  console.error(`Limit broken: p95 ${p95.toFixed(1)} ms (max ${maxP95}), error rate ${errorRate.toFixed(4)} (max ${maxErrorRate})`)
  process.exit(1)
}
