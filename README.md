# Bas.Api

Activity-statement (BAS/GST) lodgement for partner platforms. A partner's users prepare their
quarterly statement inside the partner's own app; this service holds the data, and pushes it into
Xero Practice Manager where a registered agent reviews and lodges it.

It exists as its own service rather than as part of NightTax because NightTax is organised around
annual income tax returns and has no identity for a worker who never filed one — which is every
worker arriving through a partner. See `docs/bas-gateway.md` in the NightTax repo for the reasoning.

## Shape

```
Partner platform (Next.js web + Flutter)
      |  their server, server-to-server (never the browser)
      v
  Bas.Api              REST + OpenAPI - partner auth - own Postgres
      |                Partner - PartnerUserLink - Worker - BasPeriod - SyncState
      |                reconciler owns retry, so a partner call never waits on a browser
      v  gRPC + x-api-key
  PracticeManager.Api  the Xero session, one browser, queue of one
      v
  Xero Practice Manager -> agent reviews -> lodges to the ATO
```

## Layout

```
src/
  Contracts/        Bas.Api.Contracts (NuGet) - wire types partners and .NET consumers share
  Api/              REST surface, partner auth, EF model + migrations
  ServiceDefaults/  OpenTelemetry, health checks, service discovery
  AppHost/          Aspire -> docker-compose + Caddy labels, and a Postgres for local work
tests/
  Api.Tests/        the suite, against a real Postgres via Testcontainers
docs/
  partner-integration.md   send this to the partner's engineers
```

## Build status

Phase **3a is complete**: the service scaffold and the whole partner authentication path.
Phases 3b onward (worker identity, BAS periods, the reconciler, status webhooks, partner admin)
are not built yet — see *What isn't here yet* below.

## Authentication

A partner's server exchanges two JWTs it signs itself for a short-lived bearer token scoped to one
of its users. `docs/partner-integration.md` is the partner-facing version of this; the short
internal version:

- **No shared secret.** Partners register a public key and sign with the private half
  (`private_key_jwt`). Nothing secret crosses the wire, so nothing secret can leak from either side.
- **Identity resolves on `(partner_id, partner_sub)` only — never on email.** A unique index
  enforces it. If an assertion could resolve a user by email address, anyone able to sign one could
  name a victim and be handed that person's TFN and figures.
- **No refresh token ever reaches a browser.** Renewal is the partner's component re-calling the
  partner's own token route, which their session already guards — so a user logging out of the
  partner app revokes our access for free, with no revocation machinery on either side.
- **Ten-minute tokens**, signed asymmetrically with a key persisted encrypted at rest.
- **Scope is the boundary**, checked server-side on every endpoint. Which components a partner
  imports is cosmetic; a token holder can call any route it can name.

### Design notes

**Why not ASP.NET Core Identity.** There are no local accounts here — nobody sets a password and
nobody logs in. Identity would contribute seven unused tables and one real hazard: its external
login path links by matching email, which is the exact vector the design above exists to close.

**Why not a full OAuth server (OpenIddict, Duende).** The surface is one grant type, two JWTs in,
one token out — no redirects, no consent, no PKCE, no refresh, no revocation. The part such a server
would genuinely save is JWKS publication and key rotation, and neither is needed: nobody but this
service verifies these tokens. If introspection or refresh tokens ever arrive, switching is a rework
of `src/Api/Auth/` alone; `Partner`, `PartnerUserLink` and `Worker` are unaffected.

**Why the partner's key is in configuration rather than fetched from a JWKS URL.** A JWKS URL buys
self-service rotation, at the cost of an outbound request to an address someone else controls — and
so a cache, a negative cache, a response size cap, an SSRF guard, and rate-limited refresh on an
unrecognised key id. For a handful of partners rotating about as often as they change bank accounts,
holding the key directly is the better trade. Rotation becomes: they send the new public key, we
redeploy configuration.

**Why there is no `/.well-known/jwks.json`.** Nothing consumes it. Partners never inspect our
tokens; they hand them to a browser, which hands them back, and we verify them in-process.

## Registering a partner

Until the admin API lands (phase 3e), partners are declared in configuration and reconciled into the
database at startup. Nothing here is secret:

```json
{
  "Partners": {
    "Registrations": [
      {
        "ClientId": "mygigsters",
        "Name": "MyGigsters",
        "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\nMIIBIjANBgkq...\n-----END PUBLIC KEY-----",
        "AllowedScopes": "bas:read bas:write profile:write",
        "Active": true
      }
    ]
  }
}
```

Reconciliation is **additive**: a registration present is created or updated, one that's absent is
left alone. Silently orphaning a partner's worker links because of a config typo would be worse than
leaving a stale row. `"Active": false` is the kill switch — exchange starts failing immediately, and
tokens already issued expire within minutes on their own.

## Configuration

| Setting | Source |
| --- | --- |
| `ConnectionStrings__basdb` | Aspire (env in the container) |
| `Security__DataEncryptionKey` | env / user secrets. **Required outside Development.** |
| `PartnerAuth__Issuer` | env; this service's public origin |
| `Partners__Registrations__<n>__*` | env / appsettings |
| `Cors__AllowedOrigins__<n>` | env; the partner origins our JS is embedded on |

`Security__DataEncryptionKey` is a base64 256-bit key protecting the signing key at rest, and worker
TFNs from phase 3b:

```bash
openssl rand -base64 32
```

**Losing it means every stored signing key becomes undecryptable** and has to be regenerated, which
invalidates every token in flight. It belongs in the deployment secret store and must outlive any
single container. Outside Development, a missing key fails the deploy rather than being quietly
generated — a service that starts cleanly and then can't decrypt its own keys after the next restart
is the worse outcome.

Local development:

```bash
cp src/Api/appsettings.Development.json.example src/Api/appsettings.Development.json
dotnet run --project src/AppHost
```

The AppHost brings up Postgres and applies migrations on startup.

## Tests

```bash
dotnet test tests/Api.Tests/Api.Tests.csproj
```

**Requires a running Docker daemon.** The suite runs against a real Postgres via Testcontainers,
which costs about ten seconds of container start. That's deliberate: an in-memory SQLite would
remove the dependency but only by giving up the two things this suite exists for — the real EF
migrations are applied exactly as a deploy applies them, and writes genuinely run in parallel, so
the unique index arbitrating concurrent provisioning is actually exercised rather than serialised
into a no-op.

The service's clock is `TimeProvider` throughout, including inside JWT lifetime validation, so
expiry and replay are tested without waiting.

## Deploying

`.github/workflows/deploy.yml` (manual dispatch) runs the tests, publishes the contracts package,
builds the image, generates the compose file with `aspire publish`, and ships it to the same
Lightsail box as `PracticeManager.Api`, behind the same Caddy.

Required secrets: `POSTGRES_PASSWORD`, `DATA_ENCRYPTION_KEY`, `DEPLOY_SSH_KEY`.
Required variables: `SERVER_HOST`, `SERVER_USER`, `BAS_REMOTE_DIR`.

Postgres runs as a compose service with a named volume. It holds worker identity and, from 3b, TFNs
— back it up.

## What isn't here yet

| Phase | Work |
|---|---|
| 3b | `Worker` identity fields + `BasPeriod` + the REST surface + OpenAPI schemas |
| 3c | Reconciler calling `SyncActivityStatement` on PracticeManager.Api |
| 3d | Status webhook + net-amount read-back |
| 3e | Partner admin API (register, rotate, suspend) + per-request audit |

`Worker` currently carries an id and a creation timestamp and nothing else — enough for the token's
`sub` to be a subject this service owns rather than one a partner supplied. The identity fields
Practice Manager needs arrive with 3b.

Two questions from the plan are still open and both gate 3c: who picks the statement type (it should
probably be read back from ATO prefill rather than supplied by the partner), and who captures the
taxpayer's declaration.
