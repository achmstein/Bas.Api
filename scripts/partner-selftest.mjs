/**
 * FOR THE PARTNER. Proves your signing key works against the live API, before you build anything.
 *
 *   npm install jose
 *   BAS_SIGNING_KEY="$(cat bas-signing.key)" node partner-selftest.mjs mygigsters
 *
 * It runs the whole flow for a made-up worker: gets a token, reads the worker back, saves figures,
 * reads them back. It deliberately does NOT submit - submitting queues a real lodgement with the
 * practice, and this is a made-up person.
 */

import { SignJWT, importPKCS8 } from 'jose'
import { randomUUID } from 'node:crypto'

const BAS = process.env.BAS_URL ?? 'https://bas.nighttax.com.au'
const CLIENT_ID = process.argv[2]
const SUBJECT = process.argv[3] ?? 'selftest-worker'
const PRIVATE_KEY = process.env.BAS_SIGNING_KEY

if (!CLIENT_ID || !PRIVATE_KEY) {
  console.error('usage: BAS_SIGNING_KEY="$(cat bas-signing.key)" node partner-selftest.mjs <client-id> [subject]')
  process.exit(64)
}

let failures = 0

function check(label, ok, detail = '') {
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${label}${detail ? `  ->  ${detail}` : ''}`)
  if (!ok) failures++
}

/** Both JWTs are the same shape. Only `sub` differs: your platform, or one of your users. */
async function sign(subject) {
  const key = await importPKCS8(PRIVATE_KEY, 'RS256')
  return new SignJWT({})
    .setProtectedHeader({ alg: 'RS256' })
    .setIssuer(CLIENT_ID)
    .setSubject(subject)
    .setAudience(BAS)
    .setJti(randomUUID())
    .setIssuedAt()
    .setExpirationTime('2m')
    .sign(key)
}

async function getToken(scope) {
  const body = new URLSearchParams({
    grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
    client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
    client_assertion: await sign(CLIENT_ID),
    subject_token_type: 'urn:ietf:params:oauth:token-type:jwt',
    subject_token: await sign(SUBJECT),
  })
  if (scope) body.set('scope', scope)

  const response = await fetch(`${BAS}/api/v1/partner/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body,
  })

  return { status: response.status, body: await response.json().catch(() => ({})) }
}

console.log(`Checking ${CLIENT_ID} against ${BAS}\n`)

const token = await getToken()

if (token.status !== 200) {
  check('token exchange', false, `HTTP ${token.status} ${token.body.error ?? ''}`)
  console.error(`
Most likely one of:
  - We have not registered ${CLIENT_ID} yet, or registered a different client id.
  - The public key we hold does not match the private key in BAS_SIGNING_KEY.
  - Your clock is off by more than 30 seconds.
`)
  process.exit(1)
}

check('token exchange', true, token.body.scope)
check('token is short-lived', token.body.expires_in <= 900, `${token.body.expires_in}s`)

const auth = { Authorization: `Bearer ${token.body.access_token}` }
const json = { ...auth, 'Content-Type': 'application/json' }

const me = await fetch(`${BAS}/api/v1/workers/me`, { headers: auth })
const worker = await me.json()
check('read the worker', me.status === 200, `worker ${String(worker.workerId).slice(0, 8)}`)

// The same subject always maps to the same worker - that is how their history survives each quarter.
const again = await getToken()
const second = await fetch(`${BAS}/api/v1/workers/me`, {
  headers: { Authorization: `Bearer ${again.body.access_token}` },
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

// Scope is enforced server-side, so a read-only token cannot write no matter what the UI allows.
const readOnly = await getToken('bas:read')
const denied = await fetch(`${BAS}/api/v1/bas/${target.financialYear}/${target.quarter}`, {
  method: 'PUT',
  headers: { Authorization: `Bearer ${readOnly.body.access_token}`, 'Content-Type': 'application/json' },
  body: JSON.stringify({ totalSales: 1 }),
})
check('a bas:read token cannot write', denied.status === 403, `HTTP ${denied.status}`)

console.log(`\n${failures === 0 ? 'All good.' : `${failures} failed.`}`)
console.log('Nothing was submitted, so nothing reached the practice.')
process.exit(failures === 0 ? 0 : 1)
