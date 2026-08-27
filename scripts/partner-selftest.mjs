/**
 * FOR THE PARTNER. Proves your API key works against the live API, before you build anything.
 *
 *   BAS_PARTNER_KEY=bas_... node partner-selftest.mjs
 *
 * No dependencies - plain Node 18+. It runs the whole flow for a made-up worker: gets a token,
 * saves an identity, saves figures, reads them back. It deliberately does NOT submit - submitting
 * queues a real lodgement with the tax practice, and this worker is not a person.
 */

const BAS = process.env.BAS_URL ?? 'https://bas.nighttax.com.au'
const KEY = process.env.BAS_PARTNER_KEY
const SUBJECT = process.argv[2] ?? 'selftest-worker'

if (!KEY) {
  console.error('usage: BAS_PARTNER_KEY=bas_... node partner-selftest.mjs [subject]')
  process.exit(64)
}

let failures = 0

function check(label, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? `  ->  ${detail}` : ''}`)
  if (!ok) failures++
}

/** Exactly what your server route does: key in the header, your user id in the body. */
async function getToken(scope) {
  const response = await fetch(`${BAS}/api/v1/partner/token`, {
    method: 'POST',
    headers: { 'x-partner-key': KEY, 'Content-Type': 'application/json' },
    body: JSON.stringify(scope ? { subject: SUBJECT, scope } : { subject: SUBJECT }),
  })
  return { status: response.status, body: await response.json().catch(() => ({})) }
}

console.log(`Checking your key against ${BAS}\n`)

const token = await getToken()

if (token.status !== 200) {
  check('get a token', false, `HTTP ${token.status} ${token.body.error ?? ''}`)
  console.error(`
Most likely one of:
  - The key was mistyped, or an old one - keys start with bas_ and rotation kills the previous one.
  - Your registration has been suspended.
Ask us to check; the answer is one click in our console.
`)
  process.exit(1)
}

check('get a token', true, token.body.scope)
check('token is short-lived', token.body.expiresIn <= 900, `${token.body.expiresIn}s`)

const auth = { Authorization: `Bearer ${token.body.accessToken}` }
const json = { ...auth, 'Content-Type': 'application/json' }

const me = await fetch(`${BAS}/api/v1/workers/me`, { headers: auth })
const worker = await me.json()
check('read the worker', me.status === 200, `worker ${String(worker.workerId).slice(0, 8)}`)

// The same subject always maps to the same worker - that is how their history survives per quarter.
const again = await getToken()
const second = await fetch(`${BAS}/api/v1/workers/me`, {
  headers: { Authorization: `Bearer ${again.body.accessToken}` },
}).then(r => r.json())
check('same user maps to the same worker', worker.workerId === second.workerId)

const identity = await fetch(`${BAS}/api/v1/workers/me`, {
  method: 'PUT',
  headers: json,
  body: JSON.stringify({
    tfn: '123456782',                 // a structurally valid test number, issued to nobody
    firstName: 'Self', familyName: 'Test',
    dateOfBirth: '1990-01-01',
  }),
})
const saved = await identity.json()
check('save the identity', identity.status === 200)
check('TFN comes back masked', saved.tfnMasked === '******782', saved.tfnMasked)
check('ready to lodge', saved.isCompleteForLodgement === true)

const quarters = await fetch(`${BAS}/api/v1/bas`, { headers: auth }).then(r => r.json())
check('list quarters', Array.isArray(quarters) && quarters.length > 0, `${quarters.length ?? 0} quarters`)

const target = quarters.find(q => q.status === 'draft') ?? quarters[0]
const put = await fetch(`${BAS}/api/v1/bas/${target.financialYear}/${target.quarter}`, {
  method: 'PUT',
  headers: json,
  body: JSON.stringify({ totalSales: 31900, gstOnSales: 2900, gstOnPurchases: 870, cashAccountingMethod: true }),
})
const period = await put.json()
check(`save figures for FY${target.financialYear} Q${target.quarter}`, put.status === 200)
check('figures read back', period.totalSales === 31900, `G1 ${period.totalSales}`)

// Scope is enforced server-side, so a read-only token cannot write no matter what a UI allows.
const readOnly = await getToken('bas:read')
const denied = await fetch(`${BAS}/api/v1/bas/${target.financialYear}/${target.quarter}`, {
  method: 'PUT',
  headers: { Authorization: `Bearer ${readOnly.body.accessToken}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ totalSales: 1 }),
})
check('a read-only token cannot write', denied.status === 403, `HTTP ${denied.status}`)

console.log(`\n${failures === 0 ? 'All good.' : `${failures} failed.`}`)
console.log('Nothing was submitted, so nothing reached the practice.')
process.exit(failures === 0 ? 0 : 1)
