# Integrating with Bas.Api

For the engineering team at a partner platform. This is everything you need to let your users
prepare and lodge a BAS through a registered tax agent, without ever showing them a second login.

About half a day's work.

---

## 1. What we need from you

Two things, neither of them secret. Email them over; there is nothing here that needs a secure
channel.

| | Example |
|---|---|
| A **client id** you'd like to use | `mygigsters` |
| Your **public signing key**, PEM | `-----BEGIN PUBLIC KEY-----\nMIIBIjAN...` |

That's the whole registration. We never hold a password, an API key, or anything else you'd have to
rotate in lockstep with us.

### Generating the key pair

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out bas-signing.key
openssl pkey -in bas-signing.key -pubout -out bas-signing.pub
```

Send us `bas-signing.pub`. Put `bas-signing.key` in your secret manager.

> **`bas-signing.key` must never reach a browser or a mobile bundle.** It stays on your server.
> Anyone holding it can request a token for any of your users.

EC keys work too, if your tooling prefers them:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out bas-signing.key
openssl ec -in bas-signing.key -pubout -out bas-signing.pub
```

---

## 2. How the flow works

```
Your user (already logged into your app)
   |
   |  opens the BAS screen
   v
YOUR SERVER  --POST /api/v1/partner/token-->  Bas.Api
   |   client_assertion: "I am <you>"          |
   |   subject_token:    "this is my user X"   +- verify both against your public key
   |                                           +- resolve X to a worker, creating one if new
   |  <---------- access token, 10 min --------+  sign with our key
   v
Browser / Flutter  --Authorization: Bearer-->  Bas.Api
```

Two JWTs, because they answer different questions. `client_assertion` says **which partner**;
`subject_token` says **which of your users**. Both are signed with your key, so you can only ever
vouch for your own users.

**Ten minutes, and no refresh token.** When it expires, your component calls *your* route again —
which is guarded by *your* session. So a user logging out of your app loses access here within
minutes, with nothing to build on either side.

---

## 3. The two JWTs

Both are signed with your private key, using `RS256` (or `ES256` for an EC key). Set a `kid` header
if you like; we don't require one.

### `client_assertion`

```jsonc
{
  "iss": "mygigsters",                      // your client id
  "sub": "mygigsters",                      // must equal iss (RFC 7523 section 3)
  "aud": "https://bas.nighttax.com.au",     // us
  "jti": "9f2c1e...",                       // unique per call
  "iat": 1756272000,
  "exp": 1756272120                         // at most 5 minutes after iat
}
```

### `subject_token`

```jsonc
{
  "iss": "mygigsters",
  "sub": "4471",                            // YOUR stable internal id for the user
  "aud": "https://bas.nighttax.com.au",
  "jti": "3a77b0...",                       // unique per call
  "iat": 1756272000,
  "exp": 1756272120
}
```

### Rules that will bite you if missed

- **`sub` must be a stable internal id.** Never an email address, a phone number, or anything a user
  can change. It is the permanent key to that person's tax records — if it changes, they become a
  different person to us and lose their history.
- **`jti` must be fresh on every call.** We reject a repeat.
- **`exp - iat` must be five minutes or less.**
- **Both tokens need `iat` and `exp`.** A missing one is a rejection, not a default.

---

## 4. The token request

`POST /api/v1/partner/token`, `application/x-www-form-urlencoded`.

| Field | Value |
|---|---|
| `grant_type` | `urn:ietf:params:oauth:grant-type:token-exchange` |
| `client_assertion_type` | `urn:ietf:params:oauth:client-assertion-type:jwt-bearer` |
| `client_assertion` | the first JWT |
| `subject_token_type` | `urn:ietf:params:oauth:token-type:jwt` |
| `subject_token` | the second JWT |
| `scope` | optional; omit to receive everything you were granted |

Success:

```json
{
  "access_token": "eyJhbGciOiJSUzI1NiIs...",
  "issued_token_type": "urn:ietf:params:oauth:token-type:access_token",
  "token_type": "Bearer",
  "expires_in": 600,
  "scope": "bas:read bas:write profile:write"
}
```

Failure is a standard OAuth error, `{ "error": "...", "error_description": "..." }`:

| `error` | Status | Means |
|---|---|---|
| `invalid_client` | 401 | We couldn't authenticate you: unknown client id, wrong key, expired or replayed assertion, or you've been suspended. Deliberately the same answer for all of them. |
| `invalid_grant` | 400 | The `subject_token` isn't acceptable — usually a missing `sub`. |
| `invalid_scope` | 400 | You asked for a scope you don't hold. |
| `invalid_request` | 400 | A field is missing or has the wrong constant. |
| `unsupported_grant_type` | 400 | `grant_type` isn't token exchange. |

---

## 5. Next.js

```ts
// app/api/bas-token/route.ts  — YOUR route, guarded by YOUR session
import { SignJWT, importPKCS8 } from 'jose'
import { auth } from '@/lib/auth'

const CLIENT_ID = 'mygigsters'
const BAS = 'https://bas.nighttax.com.au'

async function sign(subject: string) {
  const key = await importPKCS8(process.env.BAS_SIGNING_KEY!, 'RS256')
  return new SignJWT({})
    .setProtectedHeader({ alg: 'RS256' })
    .setIssuer(CLIENT_ID)
    .setSubject(subject)
    .setAudience(BAS)
    .setJti(crypto.randomUUID())
    .setIssuedAt()
    .setExpirationTime('2m')
    .sign(key)
}

export async function POST() {
  const session = await auth()
  if (!session?.user) return Response.json({ error: 'unauthorised' }, { status: 401 })

  // This must be your stable internal id, not their email.
  const [clientAssertion, subjectToken] = await Promise.all([
    sign(CLIENT_ID),
    sign(session.user.id),
  ])

  const response = await fetch(`${BAS}/api/v1/partner/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({
      grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
      client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
      client_assertion: clientAssertion,
      subject_token_type: 'urn:ietf:params:oauth:token-type:jwt',
      subject_token: subjectToken,
    }),
  })

  if (!response.ok) {
    console.error('bas token exchange failed', await response.text())
    return Response.json({ error: 'upstream' }, { status: 502 })
  }

  const { access_token, expires_in } = await response.json()
  return Response.json({ token: access_token, expiresIn: expires_in })
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

Re-invoke `getToken()` when you're within about a minute of expiry.

---

## 6. Flutter

Identical shape — your app calls *your* backend, which does the exchange. The signing key stays on
your server; it never ships in the bundle.

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

---

## 7. Checking it works

Once we've registered you, the smallest end-to-end test:

```bash
curl -s https://bas.nighttax.com.au/api/v1/workers/me \
  -H "Authorization: Bearer $TOKEN"
```

```json
{ "workerId": "0198f2c1-...", "partnerId": "mygigsters" }
```

`workerId` is ours, and it's stable for a given `sub`. Call the exchange twice for the same user and
you'll get the same `workerId` back — that's how we know it's the same person next quarter.

---

## 8. Scopes

| Scope | Grants |
|---|---|
| `bas:read` | Read the user's own activity statements |
| `bas:write` | Save figures and submit for lodgement |
| `profile:write` | Set the user's identity: TFN, ABN, name, date of birth |

Ask for the narrowest set a given screen needs. You can always request a subset of what you hold;
you can never request more.

---

## 9. Full API reference

The OpenAPI document is served at `/openapi/v1.json`. Point your generator at it:

```bash
npx openapi-typescript https://bas.nighttax.com.au/openapi/v1.json -o src/lib/bas-api.d.ts
```

For Dart, configure `openapi_generator` against the same URL.

---

## 10. Rotating your key

Send us the new public key. We deploy it, and from that moment assertions signed with the old key
stop being accepted — so sign with the new one only once we've confirmed.

If you need a zero-gap rotation, tell us: we can register the new key alongside the old one for a
window. Given a ten-minute token lifetime, a brief coordinated switch is usually simpler than the
overlap.
