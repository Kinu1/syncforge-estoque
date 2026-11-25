import assert from 'node:assert/strict'
import { createHash, randomUUID } from 'node:crypto'
import { setTimeout as delay } from 'node:timers/promises'

const args = process.argv.slice(2)
const mixed = args.includes('--mixed')
const transfer = args.includes('--transfer')
const base = new URL(args.find((arg) => !arg.startsWith('--')) ?? 'http://127.0.0.1:8080')

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

async function prepareStock(operator) {
  if (mixed) {
    const scenario = await operator('/demo/scenario', { method: 'POST', value: { scenario: 'mixed' } })
    assert.equal(scenario.status, 204, JSON.stringify(scenario.payload))
    return
  }

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

  const confirmed = await operator('/imports/' + imported.payload.id + '/confirm', { method: 'POST' })
  assert.equal(confirmed.status, 204)
}

const operator = createClient()
const started = await operator('/demo/session', { method: 'POST' })
assert.equal(started.status, 200, JSON.stringify(started.payload))
const expected = transfer ? ['synchronized', 'synchronized'] :
  mixed ? ['conflict', 'synchronized', 'synchronized'] : ['synchronized']

async function prepareBatch() {
  if (transfer) {
    const reset = await operator('/demo/scenario', { method: 'POST', value: { scenario: 'transfer' } })
    assert.equal(reset.status, 204)
    const view = await operator('/allocations')
    assert.equal(view.status, 200)
    const [stock] = view.payload.stocks
    const target = (channel, quantity) => {
      const { listingId, version } = view.payload.listings.find((row) => row.channel === channel && row.sku === stock.sku)
      return { listingId, version, quantity }
    }
    return { path: '/allocations/' + stock.sku, value: { revision: stock.revision, channelA: target('A', 2), channelB: target('B', 5) } }
  }

  await prepareStock(operator)

  const divergences = await operator('/divergences')
  assert.equal(divergences.status, 200)
  const pending = divergences.payload.filter((row) => row.status === 'divergent')
  assert.equal(pending.length, expected.length)
  assert.equal(pending.find((row) => row.sku === 'DEMO-001').availableQuantity, 7)
  const items = pending.map(({ sku, revision, observedVersion }) => ({ sku, revision, observedVersion }))
  return { path: '/sync-batches', value: { items } }
}

const { path, value } = await prepareBatch()
const key = randomUUID()
const created = await operator(path, { method: 'POST', value, key })
assert.equal(created.status, 202, JSON.stringify(created.payload))
const batchPath = '/sync-batches/' + encodeURIComponent(created.payload.id)
const replay = await operator(path, { method: 'POST', value, key })
assert.equal(replay.payload?.id, created.payload.id)

const deadline = Date.now() + (mixed || transfer ? 85_000 : 30_000)
let result
let statuses
do {
  result = await operator(batchPath)
  assert.equal(result.status, 200)
  statuses = result.payload.items.map((item) => item.status).sort()
  if (statuses.join(',') === expected.join(',')) break
  await delay(mixed ? 1_500 : 600)
} while (Date.now() < deadline)
assert.deepEqual(statuses, expected, JSON.stringify(result.payload))
assert.ok(result.payload.items.every((item) => item.attempts.length > 0))

const visitor = createClient()
assert.equal((await visitor('/demo/session', { method: 'POST' })).status, 200)
assert.equal((await visitor(batchPath)).status, 404)

if (transfer) {
  const final = await operator('/allocations')
  assert.equal(final.status, 200)
  const channels = final.payload.listings.filter((item) => item.sku === 'DEMO-001')
  assert.deepEqual(channels.map((item) => [item.channel, item.quantity]), [['A', 2], ['B', 5]])
  const increase = result.payload.items.find((item) => item.channel === 'B')
  assert.ok(increase.attempts.some((attempt) => attempt.status === 'waitingForReduction'))
  console.log('Transferência confirmada após recuperar a resposta da redução. Saldos finais A = 2 e B = 5.')
} else console.log(mixed
  ? 'Lote misto confirmado, repetição idempotente e sessões isoladas.'
  : 'Importação, confirmação do estoque, repetição idempotente e isolamento verificados.')
