// A day of the field team against a TEST copy of the API (plan step 5.70), with no dependencies:
//   node scripts/perf/scenario.mjs --url http://127.0.0.1:5080 --visitors 50 --minutes 5 --speed 1 [--pid <api process id>] [--max-p95-ms 800] [--max-error-rate 0.01]
// Needs the data of scripts/perf/seed-load.sql and the same signing key as the API (--key, --issuer, --audience; the defaults match scripts/perf/README.md).
// Every visitor: starts a shift, sends position batches with the phone's health, polls the unread count, reads the plan, and goes through visits (on the way, arrive, start, report, complete at the customer's
// place) and places orders (no warehouse in the request: the server finds the warehouse of the customer's city). Three managers watch the live map, the plan, orders and KPIs.
// --speed 6 makes every interval six times shorter, i.e. six times the load of real life. Prints latency per route, status counts and the API's CPU and memory.
import { createHmac, randomUUID } from 'node:crypto'
import { execSync } from 'node:child_process'

const args = Object.fromEntries(process.argv.slice(2).reduce((pairs, a, i, all) => (a.startsWith('--') ? [...pairs, [a.slice(2), all[i + 1]?.startsWith('--') ? 'true' : (all[i + 1] ?? 'true')]] : pairs), []))
const base = args.url ?? 'http://127.0.0.1:5080'
const visitors = Number(args.visitors ?? 50)
const minutes = Number(args.minutes ?? 5)
const speed = Number(args.speed ?? 1)
const key = args.key ?? 'load-test-signing-key-at-least-32-bytes-long-000'
const issuer = args.issuer ?? 'load'
const audience = args.audience ?? 'load'
const maxP95 = args['max-p95-ms'] === undefined ? Infinity : Number(args['max-p95-ms'])
const maxErrorRate = args['max-error-rate'] === undefined ? 1 : Number(args['max-error-rate'])
const endAt = Date.now() + minutes * 60_000

export const lid = (kind, n) => `00000000-0000-4000-${String(kind).padStart(4, '0')}-${n.toString(16).padStart(12, '0')}`
const b64 = (x) => Buffer.from(typeof x === 'string' ? x : JSON.stringify(x)).toString('base64url')
function token(userId, familyId, deviceId) {
  const now = Math.floor(Date.now() / 1000)
  const head = b64({ alg: 'HS256', typ: 'JWT' })
  const body = b64({ sub: userId, tenant_id: lid(1, 1), session_family: familyId, device_id: deviceId, jti: randomUUID(), iss: issuer, aud: audience, nbf: now - 5, exp: now + 14 * 60 })
  return `${head}.${body}.${createHmac('sha256', key).update(`${head}.${body}`).digest('base64url')}`
}
// the place of customer n, as the seed made it
const place = (n) => ({ lon: 44.0 + ((n * 7919) % 1000) / 10000, lat: 36.15 + ((n * 104729) % 1000) / 10000 })

const stats = new Map()   // route -> { latencies, statuses }
async function call(route, method, path, auth, body, extraHeaders = {}) {
  const begin = performance.now()
  let status = 'network_error'
  let json = null
  try {
    const response = await fetch(base + path, { method, headers: { Authorization: `Bearer ${auth()}`, ...(body ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders }, body: body ? JSON.stringify(body) : undefined })
    status = String(response.status)
    const text = await response.text()
    try { json = text ? JSON.parse(text) : null } catch { json = null }
  } catch { /* counted as a network error */ }
  const entry = stats.get(route) ?? { latencies: [], statuses: new Map() }
  entry.latencies.push(performance.now() - begin)
  entry.statuses.set(status, (entry.statuses.get(status) ?? 0) + 1)
  stats.set(route, entry)
  return { status: Number(status) || 0, json }
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms / speed))
const today = new Date(Date.now() + 3 * 3600_000).toISOString().slice(0, 10)   // Iraq day
const fix = (n) => { const p = place(n); return { latitude: p.lat, longitude: p.lon, accuracyMeters: 8, capturedAt: new Date().toISOString(), isMocked: false } }

async function visitor(index) {
  const userId = lid(3, index), familyId = lid(5, index + 3), deviceId = lid(4, index + 3)
  const auth = () => token(userId, familyId, deviceId)
  await sleep(Math.random() * 60_000)   // people do not all start in the same second
  const shift = await call('POST /shifts', 'POST', '/shifts', auth)
  const shiftId = shift.json?.id
  if (!shiftId) return
  let customerNo = (index - 1) * 12 + 1
  const lastCustomer = index * 12
  let nextVisit = Date.now() + (60_000 + Math.random() * 120_000) / speed
  let nextOrderAfterVisit = 0
  let tick = 0
  while (Date.now() < endAt) {
    tick++
    const p = place(customerNo)
    // a position every 30 s, sent in one batch every minute, with the health of the phone
    if (tick % 2 === 0) {
      const points = [0, 1].map((k) => ({ clientPointId: randomUUID(), latitude: p.lat + k * 1e-4, longitude: p.lon, accuracyMeters: 12, capturedAt: new Date(Date.now() - (1 - k) * 30_000).toISOString(), isMocked: false }))
      await call('POST /shifts/{id}/points', 'POST', `/shifts/${shiftId}/points`, auth, { points, health: { appVersion: '1.0.0', platform: 'android', locationPermission: 'always', preciseLocation: true, locationServices: true, notifications: true, batteryExempt: true, batteryPercent: 70 } })
      await call('GET /notifications/unread-count', 'GET', '/notifications/unread-count', auth)
    }
    if (tick % 4 === 0) {
      await call('GET /visits (own day)', 'GET', `/visits?scheduleDate=${today}`, auth)
      await call('GET /shifts/current', 'GET', '/shifts/current', auth)
    }
    // a visit about every 3 minutes: start, report, complete at the customer's place; an order after some of them
    if (Date.now() >= nextVisit && customerNo <= lastCustomer) {
      const visitId = lid(11, customerNo)
      await call('POST /visits/{id}/en-route', 'POST', `/visits/${visitId}/en-route`, auth)
      await call('POST /visits/{id}/arrive', 'POST', `/visits/${visitId}/arrive`, auth)
      const started = await call('POST /visits/{id}/start', 'POST', `/visits/${visitId}/start`, auth, fix(customerNo), { 'Idempotency-Key': randomUUID() })
      if (started.status >= 200 && started.status < 300) {
        await sleep(20_000)
        await call('PUT /visits/{id}/report', 'PUT', `/visits/${visitId}/report`, auth, { outcome: 'products_presented', note: 'Load test visit report' })
        await call('POST /visits/{id}/complete', 'POST', `/visits/${visitId}/complete`, auth, fix(customerNo), { 'Idempotency-Key': randomUUID() })
      }
      if (Math.random() < 0.5) {
        const items = [...new Set(Array.from({ length: 3 }, () => 1 + Math.floor(Math.random() * 40)))].map((n) => ({ productId: lid(13, n), quantity: 1 + Math.floor(Math.random() * 5) }))
        await call('POST /orders', 'POST', '/orders', auth, { customerId: lid(10, customerNo), visitId, clientRef: randomUUID(), items })
      }
      customerNo++
      nextVisit = Date.now() + (150_000 + Math.random() * 60_000) / speed
    }
    await sleep(30_000)
  }
  await call('POST /shifts/{id}/end', 'POST', `/shifts/${shiftId}/end`, auth)
}

async function manager(index) {
  const auth = () => token(lid(2, index), lid(5, index), lid(4, index))
  await sleep(Math.random() * 10_000)
  let tick = 0
  while (Date.now() < endAt) {
    tick++
    await call('GET /live-map', 'GET', '/live-map', auth)
    if (tick % 2 === 0) {
      await call('GET /visit-plan (manager)', 'GET', `/visit-plan?scheduleDate=${today}`, auth)
      await call('GET /orders', 'GET', '/orders?limit=50', auth)
    }
    if (tick % 6 === 0) await call('GET /reports/kpis', 'GET', `/reports/kpis?from=${today}&to=${today}`, auth)
    await sleep(30_000)
  }
}

// CPU and memory of the API process, every 5 seconds (needs --pid)
const samples = []
let sampler = null
if (args.pid) sampler = setInterval(() => { try { const [cpu, rss] = execSync(`ps -o %cpu=,rss= -p ${args.pid}`).toString().trim().split(/\s+/).map(Number); samples.push({ cpu, rssMb: rss / 1024 }) } catch { /* process gone */ } }, 5000)

const started = performance.now()
await Promise.all([...Array.from({ length: visitors }, (_, i) => visitor(i + 1)), ...Array.from({ length: 3 }, (_, i) => manager(i + 1))])
if (sampler) clearInterval(sampler)
const seconds = (performance.now() - started) / 1000

export function percentile(sorted, p) { return sorted.length === 0 ? 0 : sorted[Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1)] }
let failures = 0, total = 0, worstP95 = 0
const rows = []
for (const [route, { latencies, statuses }] of [...stats].sort((a, b) => a[0].localeCompare(b[0]))) {
  const sorted = [...latencies].sort((a, b) => a - b)
  const bad = [...statuses].filter(([s]) => s === 'network_error' || Number(s) >= 500).reduce((n, [, c]) => n + c, 0)
  failures += bad; total += latencies.length
  const p95 = percentile(sorted, 95); worstP95 = Math.max(worstP95, p95)
  rows.push({ route, n: latencies.length, p50: +percentile(sorted, 50).toFixed(1), p95: +p95.toFixed(1), p99: +percentile(sorted, 99).toFixed(1), max: +(sorted.at(-1) ?? 0).toFixed(1), statuses: Object.fromEntries(statuses) })
}
console.table(rows.map(({ statuses, ...r }) => ({ ...r, statuses: JSON.stringify(statuses) })))
const cpu = samples.map((s) => s.cpu), rss = samples.map((s) => s.rssMb)
console.log(JSON.stringify({ visitors, minutes, speed, seconds: +seconds.toFixed(0), requests: total, requestsPerSecond: +(total / seconds).toFixed(1), serverErrors: failures, errorRate: +(failures / Math.max(1, total)).toFixed(4),
  apiCpuPercent: samples.length ? { avg: +(cpu.reduce((a, b) => a + b, 0) / cpu.length).toFixed(1), max: Math.max(...cpu) } : 'use --pid', apiMemoryMb: samples.length ? { max: +Math.max(...rss).toFixed(0) } : 'use --pid' }, null, 2))
if (worstP95 > maxP95 || failures / Math.max(1, total) > maxErrorRate) { console.error(`Limit broken: worst p95 ${worstP95.toFixed(1)} ms (max ${maxP95}), error rate ${(failures / Math.max(1, total)).toFixed(4)} (max ${maxErrorRate})`); process.exit(1) }
