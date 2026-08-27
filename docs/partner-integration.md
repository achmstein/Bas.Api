# Integrating with Bas.Api

For the engineering team at a partner platform. This is everything you need to let your users
prepare and lodge a BAS through a registered tax agent, without ever showing them a second login.

A morning's work for one backend engineer.

---

## 1. What we send you

- Your **client id** — a short name for your platform, e.g. `mygigsters`.
- Your **API key** — starts with `bas_`. It arrives over a channel we agree on.

Put the key in your secret manager as `BAS_PARTNER_KEY`. It stays on your servers — never in a
browser or an app bundle — because anyone holding it can request access for **any** of your users.
If it ever leaks, tell us: replacing it is one click on our side, and the old key stops working the
same second.

We store only a hash of the key, so it cannot be recovered from us — a replacement is always a new
key.

## 2. How it works

```
Your user (already logged into your app)
   |
   |  opens the BAS screen
   v
YOUR SERVER  --POST /api/v1/partner/token-->  Bas.Api
   |   header: x-partner-key                    |
   |   body:   { "subject": "<your user id>" }  +- resolve or create the worker
   |  <-------- access token, 10 min -----------+
   v
Browser / Flutter  --Authorization: Bearer-->  Bas.Api
```

Your key authenticates your platform; `subject` says which of your users the token is for. Back
comes a **10-minute token scoped to that one user** — that is what your page holds, so the thing
living in a browser expires in minutes while the key never leaves your server.

There is no refresh token. When the short token expires, your component calls *your* route again —
which is guarded by *your* session. So a user logging out of your app loses access here within
minutes, with nothing to build on either side.

## 3. The token request

`POST /api/v1/partner/token`

| | |
|---|---|
| Header | `x-partner-key: bas_...` |
| Body | `{"subject": "4471"}` — optionally add `"scope": "bas:read"` |

```json
{
  "accessToken": "eyJhbGciOiJSUzI1NiIs...",
  "tokenType": "Bearer",
  "expiresIn": 600,
  "scope": "bas:read bas:write profile:write"
}
```

**`subject` must be your stable internal id for the user.** Never an email, a phone number, or
anything they can change — it is the permanent key to that person's tax records here. If it
changes, they become a different person to us and lose their history.

Errors come back as `{"error": "...", "message": "..."}`:

| `error` | Status | Means |
|---|---|---|
| `invalid_key` | 401 | Wrong key, an old key after a rotation, or your registration is suspended. Deliberately the same answer for all three. |
| `invalid_request` | 400 | Usually a missing `subject`. |
| `invalid_scope` | 400 | You asked for a scope you do not hold. |

## 4. Next.js

The whole server side of the integration:

```ts
// app/api/bas-token/route.ts  - YOUR route, guarded by YOUR session
export async function POST() {
  const session = await auth()
  if (!session?.user) return Response.json({ error: 'unauthorised' }, { status: 401 })

  const response = await fetch('https://bas.nighttax.com.au/api/v1/partner/token', {
    method: 'POST',
    headers: {
      'x-partner-key': process.env.BAS_PARTNER_KEY!,
      'Content-Type': 'application/json',
    },
    // Your stable internal id, not their email.
    body: JSON.stringify({ subject: session.user.id }),
  })

  if (!response.ok) {
    console.error('bas token failed', response.status, await response.text())
    return Response.json({ error: 'upstream' }, { status: 502 })
  }

  const { accessToken, expiresIn } = await response.json()
  return Response.json({ token: accessToken, expiresIn })
}
```

On the client, hand the component a *getter*, never a stored token:

```tsx
<BasProvider getToken={async () =>
  fetch('/api/bas-token', { method: 'POST' })
    .then(r => r.json())
    .then(r => r.token)}>
  <BasReturn quarter={1} financialYear={2027} />
</BasProvider>
```

Re-invoke `getToken()` when you are within about a minute of expiry.

## 5. Flutter

Identical shape — your app calls *your* backend, which holds the key and does the exchange:

```dart
class BasTokenProvider {
  String? _token;
  DateTime _expiresAt = DateTime.fromMillisecondsSinceEpoch(0);

  Future<String> get() async {
    // Renew a minute early, so a request never starts with a token that expires mid-flight.
    if (_token != null &&
        DateTime.now().isBefore(_expiresAt.subtract(const Duration(minutes: 1)))) {
      return _token!;
    }

    final response = await http.post(
      Uri.parse('https://api.mygigsters.com/bas-token'),
      headers: {'Authorization': 'Bearer ${await yourOwnSessionToken()}'},
    );

    final body = jsonDecode(response.body) as Map<String, dynamic>;
    _token = body['token'] as String;
    _expiresAt = DateTime.now().add(Duration(seconds: body['expiresIn'] as int));
    return _token!;
  }
}
```

## 6. Check it works before you build

Run the self-test that came with this guide. Plain Node, no dependencies. It exercises the whole
flow for a made-up worker and stops short of submitting, so nothing reaches the practice.

```bash
BAS_PARTNER_KEY=bas_... node partner-selftest.mjs
```

Every line should say PASS.

## 7. Confirming a user resolves

```bash
curl -s https://bas.nighttax.com.au/api/v1/workers/me   -H "Authorization: Bearer $TOKEN"
```

```json
{ "workerId": "0198f2c1-...", "partnerId": "mygigsters" }
```

`workerId` is ours, and it is stable for a given `subject` — that is how we know it is the same
person next quarter.

---

## 8. The worker's identity

Before anything can be lodged we need enough to create a client in Practice Manager. Send it once,
whenever you have it — at signup, or the first time they open the BAS screen.

### `PUT /api/v1/workers/me` — scope `profile:write`

```json
{
  "tfn": "123 456 782",
  "abn": "51824753556",
  "firstName": "Jordan",
  "familyName": "Ellis",
  "dateOfBirth": "1994-03-12",
  "email": "jordan@example.com",
  "phone": "0400 000 000"
}
```

`tfn`, `firstName`, `familyName` and `dateOfBirth` are required; `abn`, `email` and `phone` are not.
Spaces in the TFN and ABN are fine.

Both numbers are checked against the ATO's own checksum before we store them, so a mistyped digit
comes back as a `400` **while the worker is still on the form**, rather than as a failed lodgement a
quarter later. The error never echoes the number back.

### `GET /api/v1/workers/me` — scope `bas:read`

```json
{
  "workerId": "0198f2c1-...",
  "partnerId": "mygigsters",
  "tfnMasked": "******782",
  "abn": "51824753556",
  "firstName": "Jordan",
  "familyName": "Ellis",
  "dateOfBirth": "1994-03-12",
  "isCompleteForLodgement": true
}
```

**The TFN only ever comes back masked** — to you and to us. Use `isCompleteForLodgement` to decide
whether to show the lodge button; submitting is refused while it is false.

---

## 9. Activity statements

### Financial years and quarters

Two Australian conventions that will cost you a day if you assume otherwise:

- **A financial year is named for the year it ends.** FY2027 runs 1 Jul 2026 to 30 Jun 2027.
- **Quarters are numbered from July.** Q1 is Jul–Sep; Q3 is Jan–Mar.

```
FY2027 Q1   1 Jul 2026 - 30 Sep 2026   due 28 Oct 2026
FY2027 Q2   1 Oct 2026 - 31 Dec 2026   due 28 Jan 2027
FY2027 Q3   1 Jan 2027 - 31 Mar 2027   due 28 Apr 2027
FY2027 Q4   1 Apr 2027 - 30 Jun 2027   due 28 Jul 2027
```

The due date shown is the statutory one. Lodging through a registered agent usually extends it —
that concession is one of the real reasons to go through the practice.

### `GET /api/v1/bas` — scope `bas:read`

Their statements, newest first. Quarters they have never opened are included as empty drafts, so a
first visit shows the quarter they are meant to be lodging instead of an empty list. Those carry
`"id": "00000000-0000-0000-0000-000000000000"` until the first save creates them.

### `GET /api/v1/bas/{financialYear}/{quarter}` — scope `bas:read`

One statement. An untouched quarter is an empty draft, not a `404`, so you do not need to
special-case "they have not started yet".

### `PUT /api/v1/bas/{financialYear}/{quarter}` — scope `bas:write`

```jsonc
// PUT /api/v1/bas/2027/1 - the whole payload, Simpler BAS
{
  "totalSales": 31900,        // G1, GST inclusive
  "gstOnSales": 2900,         // 1A
  "gstOnPurchases": 870,      // 1B
  "totalPurchases": 9570,     // stored, not lodged - it derives 1B
  "cashAccountingMethod": true
}
```

> **This is a full replacement, not a merge.** Send every label the worker has a value for on each
> save, not just what changed. An absent label means *this statement has no such label*, which is a
> different thing from zero.

That distinction is load-bearing. A worker with no PAYG instalment obligation has **no T section at
all**, and writing zeros into one would produce a different statement from the one the ATO issued.
So: send the whole form each time, and leave sections the worker does not have entirely absent.

Optional sections, for workers who have them:

| Field | Label | When |
|---|---|---|
| `instalmentIncome` | T1 | The ATO has them in the PAYG instalment system |
| `atoInstalmentAmount` | T7 | as above |
| `variedInstalmentAmount` | T9 | They are varying the instalment down |
| `variationReasonCode` | T4 | **Required whenever T9 is sent** |
| `totalSalaryWages` | W1 | They employ someone |
| `amountWithheld` | W2 | as above |

All amounts are **whole dollars** — the ATO drops cents on an activity statement — and none may be
negative.

Editing stops once a statement is submitted; a `409` means it is already with the practice. A
statement that *failed* can be corrected and re-submitted.

### `POST /api/v1/bas/{financialYear}/{quarter}/submit` — scope `bas:write`

```json
{
  "periodId": "0198f2d0-...",
  "status": "submitted",
  "submittedAt": "2026-10-02T04:11:09Z"
}
```

**`202 Accepted`, never `200`.** The statement is queued, not lodged. Practice Manager is a single
browser session behind a queue of one, and every worker lodges inside the same 72 hours each quarter
— a synchronous call over that would be a guaranteed outage on the busiest day of the quarter. Do
not tell your user "lodged" here; tell them "sent for review".

Submitting twice returns the original acknowledgement rather than an error, so a lost response is
safe to retry.

`409` responses worth handling:

| Detail says | Fix |
|---|---|
| Worker identity is incomplete | `PUT /api/v1/workers/me` first |
| Period has not ended | A BAS cannot be lodged before its quarter is over |
| Nothing to submit | Save at least G1, 1A and 1B |

### `GET /api/v1/bas/{financialYear}/{quarter}/status` — scope `bas:read`

```json
{ "status": "in_review", "netAmount": 2030, "dueDate": "2026-10-28" }
```

```
draft -> submitted -> awaiting_statement -> pushed -> in_review -> lodged
                 \-> failed  (carries failureReason; correct it and re-submit)
```

| Status | Means |
|---|---|
| `draft` | Being filled in |
| `submitted` | Queued for the practice |
| `awaiting_statement` | The ATO has not issued the statement for this period yet. Not an error, and nothing the worker can do — show it as "waiting on the ATO", not as a problem. |
| `pushed` | In Practice Manager, waiting for the agent |
| `in_review` | The agent has it open |
| `lodged` | Lodged with the ATO |
| `failed` | Did not reach the practice; `failureReason` says why |

There is deliberately no `statementType` in the request. The ATO issues the activity statement and
chooses its type from obligations neither of us can see, so nobody upstream of the ATO gets to
assert one — we read it back from the statement the ATO actually issued and return it to you. Until
then it is `null`.

**`netAmount` is label 9 as Practice Manager computed it**, not as we did — it is `null` until the
statement has been pushed and read back. We deliberately do not calculate it locally: if our
arithmetic and the ATO's ever disagree, ours is the one that is wrong.

---

## 10. Scopes

| Scope | Grants |
|---|---|
| `bas:read` | Read the worker's own statements and identity |
| `bas:write` | Save figures and submit for lodgement |
| `profile:write` | Set the worker's identity: TFN, ABN, name, date of birth |

Ask for the narrowest set a given screen needs. You can always request a subset of what you hold;
you can never request more. A missing scope is a `403`, not a `401`.

---

## 11. Full API reference

The OpenAPI document is served at `/openapi/v1.json`. Point your generator at it:

```bash
npx openapi-typescript https://bas.nighttax.com.au/openapi/v1.json -o src/lib/bas-api.d.ts
```

For Dart, configure `openapi_generator` against the same URL.

---

## 12. Two things that are not code

Both have real-world lead time, so they are worth starting before the integration is finished.

**Every worker must be on the practice's ATO client list** before anything can be lodged for them.
That is per-worker onboarding with a genuine delay — it belongs in your signup flow, not at the
moment someone taps "lodge".

**We need a valid TFN per worker.** Practice Manager will not create a client without one. That
brings the Privacy Act TFN Rule onto both sides, so the data-sharing agreement is best drafted
alongside the build rather than after it.

---

## 13. If your key leaks

Tell us. Replacing it is one click on our side; the old key stops working the same second, and we
send you the new one. Your calls fail in between, which is the point.

## 14. Status webhooks (optional)

Polling `GET /api/v1/bas/{fy}/{q}/status` works whether or not you set this up, so nothing is
blocked on it. Webhooks just mean you find out sooner.

Send us a URL, and we will POST to it when a statement changes status. We issue the signing secret when we configure it, and send it to you with the confirmation.

### What arrives

```http
POST /your/webhook/endpoint
Content-Type: application/json
X-Bas-Event: bas.status_changed
X-Bas-Delivery: 0198f2d0-4c31-7a02-9b55-1e0f7b3a9d21
X-Bas-Signature: t=1759372269,v1=6f8c0b0e...
```

```json
{
  "event": "bas.status_changed",
  "deliveryId": "0198f2d0-4c31-7a02-9b55-1e0f7b3a9d21",
  "occurredAt": "2026-10-02T04:11:09Z",
  "workerId": "0198f2c1-...",
  "partnerSub": "4471",
  "financialYear": 2027,
  "quarter": 1,
  "status": "in_review",
  "previousStatus": "pushed",
  "netAmount": 2030
}
```

`partnerSub` is **your** id for the worker, so you need no lookup table on your side.

There are deliberately no tax figures in the body. A webhook passes through logs and proxies neither
of us controls, and you already hold a token — fetch the detail if you need it.

### Verifying the signature

`X-Bas-Signature` is `t=<unix seconds>,v1=<hex HMAC-SHA256>`. The signed material is
`"{t}.{raw request body}"`, keyed with the secret you gave us. Same shape as Stripe's, so if you
have done that one you have done this one.

```ts
import { createHmac, timingSafeEqual } from 'node:crypto'

export function verify(rawBody: string, header: string, secret: string) {
  const parts = Object.fromEntries(header.split(',').map(p => p.split('=', 2)))
  const timestamp = Number(parts.t)

  // Reject anything older than five minutes: without this, a captured request can be replayed
  // at any point in the future and will still verify.
  if (!Number.isFinite(timestamp) || Math.abs(Date.now() / 1000 - timestamp) > 300) return false

  const expected = createHmac('sha256', secret).update(`${parts.t}.${rawBody}`).digest('hex')
  const a = Buffer.from(expected)
  const b = Buffer.from(parts.v1 ?? '')

  return a.length === b.length && timingSafeEqual(a, b)
}
```

Verify against the **raw body**, before any JSON parsing. Re-serialising changes the bytes and the
signature will not match.

### Rules for your endpoint

- **Answer 2xx quickly.** Acknowledge, then do the work. We time out at ten seconds.
- **Deduplicate on `X-Bas-Delivery`.** Delivery is at least once: a slow answer, or one that arrives
  after the connection dropped, means you will be told twice.
- **Do not assume order.** Retries can arrive after a later event. `previousStatus` and
  `occurredAt` let you discard one that has been overtaken.
- **A 4xx makes us stop** (except 408 and 429). We read it as "this request is not wanted" and give
  up on that delivery — you can still poll. A 5xx or a timeout is retried, backing off from 30
  seconds to six hours, ten attempts.

If we cannot reach you at all, nothing is lost: the statement still progresses, and the status
endpoint still tells you where it got to.
