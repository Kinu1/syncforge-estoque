import assert from 'node:assert/strict'
import { createHash, randomUUID } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'

const base = new URL(process.argv[2] ?? 'http://127.0.0.1:8080')

function createClient() {
  let cookie

  return async (path, { method = 'GET', value, key } = {}) => {
    const body = value === undefined ? undefined : JSON.stringify(value)
    const headers = {}

    if (cookie) headers.cookie = cookie
    if (body !== undefined) headers['content-type'] = 'application/json'
    if (key) headers['idempotency-key'] = key
    if (method !== 'GET') {
      headers.origin = base.origin
      headers['sec-fetch-site'] = 'same-origin'
      headers['x-amz-content-sha256'] = createHash('sha256').update(body ?? '').digest('hex')
    }

    const response = await fetch(new URL('/api/v1' + path, base), {
      method,
      headers,
      body,
      signal: AbortSignal.timeout(12_000),
    })
    const setCookie = response.headers.getSetCookie()[0]
    if (setCookie) cookie = setCookie.split(';', 1)[0]
    const payload = response.headers.get('content-type')?.includes('json')
      ? await response.json()
      : undefined
    return { status: response.status, payload }
  }
}

const operator = createClient()
assert.equal((await operator('/demo/session', { method: 'POST' })).status, 200)

const imported = await operator('/imports', {
  method: 'POST',
  value: {
    capturedAt: new Date().toISOString(),
    csv: 'sku,physical_quantity,reserved_quantity\nDEMO-001,12,5\n',
  },
})
assert.equal(imported.status, 201, JSON.stringify(imported.payload))
assert.deepEqual(imported.payload.errors, [])
assert.equal(imported.payload.rows[0].availableQuantity, 7)
assert.equal((await operator('/imports/' + imported.payload.id + '/confirm', { method: 'POST' })).status, 204)

const divergences = await operator('/divergences')
assert.equal(divergences.status, 200)
const row = divergences.payload.find((row) => row.sku === 'DEMO-001')
assert.equal(row.availableQuantity, 7)
assert.equal(row.status, 'divergent')

const key = randomUUID()
const items = [{ sku: row.sku, revision: row.revision, observedVersion: row.observedVersion }]
const created = await operator('/sync-batches', { method: 'POST', value: { items }, key })
assert.equal(created.status, 202, JSON.stringify(created.payload))
const batchPath = '/sync-batches/' + encodeURIComponent(created.payload.id)
const replay = await operator('/sync-batches', { method: 'POST', value: { items }, key })
assert.equal(replay.payload?.id, created.payload.id)

const deadline = Date.now() + 30_000
let item
do {
  const result = await operator(batchPath)
  assert.equal(result.status, 200)
  item = result.payload.items[0]
  if (item.status === 'synchronized') break
  await delay(600)
} while (Date.now() < deadline)
assert.equal(item.status, 'synchronized', JSON.stringify(item))
assert.ok(item.attempts.length > 0)

const visitor = createClient()
assert.equal((await visitor('/demo/session', { method: 'POST' })).status, 200)
assert.equal((await visitor(batchPath)).status, 404)
console.log('Importação, confirmação do estoque, repetição idempotente e isolamento verificados.')
