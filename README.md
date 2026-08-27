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
  Api/              Statements/ Auth/ Sync/ Webhooks/ Admin/ - each slice keeps its own
                    endpoints, services and options together; Data/ holds the shared model
  ServiceDefaults/  OpenTelemetry, health checks, service discovery
  AppHost/          Aspire -> docker-compose + Caddy labels, and a Postgres for local work
tests/
  Api.Tests/        the suite, against a real Postgres via Testcontainers
docs/
  partner-integration.md   send this to the partner's engineers
```

## Build status

Phases **3a, 3b, 3c, 3e complete; 3d all but one piece**: the scaffold, partner authentication, the
REST surface, the reconciler that pushes into Practice Manager, and signed status webhooks. A
statement travels `draft -> submitted -> awaiting_statement -> pushed` on its own, and the partner
is told at each step.

There is an admin console at `/admin` (sign in) and a REST surface at `/admin/v1` (named API key).
The read-back is written on the Practice Manager side — label 9, the statement type the ATO issued,
and which sections the statement carries — but **wiring it here needs a `PracticeManager.Api.Contracts`
release first**, because the response fields do not exist in the published package yet. See
*What is not here yet*.

## The surface

| Endpoint | Scope | Purpose |
|---|---|---|
| `POST /api/v1/partner/token` | — | Token exchange. Server-to-server only. |
| `GET /api/v1/workers/me` | `bas:read` | The worker behind the token. TFN always masked. |
| `PUT /api/v1/workers/me` | `profile:write` | Worker identity. TFN and ABN checksum-validated. |
| `GET /api/v1/bas` | `bas:read` | Their statements, newest first. |
| `GET /api/v1/bas/{fy}/{q}` | `bas:read` | One statement; an empty draft if untouched. |
| `PUT /api/v1/bas/{fy}/{q}` | `bas:write` | Save figures. **Full replacement, not a merge.** |
| `POST /api/v1/bas/{fy}/{q}/submit` | `bas:write` | **202.** Queued, never lodged inline. |
| `GET /api/v1/bas/{fy}/{q}/status` | `bas:read` | Status + the net amount PM computed. |

`docs/partner-integration.md` is the partner-facing version, with working Next.js and Dart.
Browsable reference docs are at `/docs`, generated from `/openapi/v1.json`.

The admin document is served at `/openapi/admin.json` but only to an authenticated caller. Every
route on it is protected either way; publishing a map of the operations surface just serves nobody
outside the practice.

### Rules worth knowing before reading the code

- **Money is whole-dollar `int`.** The ATO drops cents on an activity statement, so carrying
  decimals would only invite a rounding disagreement with the ATO's own arithmetic.
- **Every figure is nullable, and the distinction is load-bearing.** A worker with no PAYG
  instalment obligation has no T section at all — which is not a T section of zero. `PUT` is
  therefore a full replacement: an absent label means the statement has no such label.
- **`netAmount` is read back from Practice Manager, never calculated here.** If our arithmetic and
  the ATO's disagree, ours is the one that is wrong.
- **Submit is asynchronous, always.** PM is one browser session behind a queue of one, and BAS is
  quarterly — every worker lodges inside the same 72 hours. A synchronous call over that is a
  guaranteed outage on the busiest day of the quarter.
- **TFN validation happens at save, not at push.** PM creates a client in two calls and only the
  second validates the TFN, so a bad one leaves a fully-created client behind — and a retrying
  reconciler orphans another on every attempt.
- **Financial years are named for the year they end**, and quarters run from July. FY2027 Q1 is
  Jul-Sep 2026.

## The push

`PracticeManager.Api` has no job queue by design: the caller already owns a durable record of what
needs syncing, and duplicating that server-side would mean two systems disagreeing about the same
work. This service is that caller, so retry is ours. `SyncState` is the ledger — subject, status,
`DirtyAt`, `ContentHash`, `AttemptCount`, `NextAttemptAt`, `LastError` — lifted from NightTax, which
has been running the same bargain against the same downstream for a while.

It is kept separate from `BasPeriod.Status` on purpose. That status is the business fact a partner
reads; this is retry bookkeeping. Merging them would leak plumbing into the wire contract and make
every schedule change a breaking one.

**Find, never create.** The snapshot goes out with a *blank* `statementType`, which
`PracticeManager.Api` reads as "find the statement the ATO issued; do not create one". The ATO
chooses the type from obligations neither service can see, and PM will create a statement of
whatever type it is told without complaint — so a guess produces a wrong statement in the live
practice that nobody notices until the agent opens it. When no statement exists the push returns
`taxReturnId = 0`, the period goes to `awaiting_statement`, and it is re-checked in hours.

**One at a time.** PM is a single browser session behind a queue of one. Concurrency here would not
make anything faster; it would move the queue upstream and make failures harder to read.

**Three outcomes, three responses.** A rejection spends the attempt budget and backs off. Practice
Manager being unavailable — its `FAILED_PRECONDITION`, meaning it will not let us in — backs off but
spends nothing, because an outage says nothing about this statement. A statement the ATO has not
issued spends nothing either, and waits hours rather than minutes.

**Unchanged content is not re-pushed.** `ContentHash` covers the figures *and* the identity, so
correcting a misspelled surname reaches the practice while a redeploy costs nothing. That matters
when every push consumes a slot on a session with a ten-minute cold start.

## Telling the partner

Every status change is queued as a `WebhookDelivery` in the same transaction as the change itself,
so a submit that rolls back cannot leave a webhook promising something that never happened. A
separate dispatcher delivers them.

**Optional, and never on the critical path.** A partner with no registered URL simply polls, and
polling keeps working even when delivery has been abandoned — so a statement is never stuck because
a webhook did not arrive. Failures log at warning, not error, for that reason.

**Signed, not merely posted.** HMAC-SHA256 over `timestamp.payload`, in `X-Bas-Signature`, Stripe's
shape. The timestamp is inside the signed material so a captured request cannot be replayed for
ever. A shared secret is appropriate here in a way it is not on the token endpoint: leaking it lets
someone send a partner a false status update — bounded, and not a disclosure — rather than mint
tokens for any worker.

**No tax figures in the payload.** It carries which statement changed and to what. A webhook passes
through logs and proxies we do not control, and the partner already holds a token.

**At least once**, with `X-Bas-Delivery` to deduplicate on. A 4xx other than 408/429 stops delivery
immediately: repeating a request the partner has rejected will not start working.

## Authentication

A partner's server sends its API key and one of its user ids; back comes a ten-minute bearer token
scoped to that user, which is what their page holds. `docs/partner-integration.md` is the
partner-facing version; the short internal one:

- **The key is never stored.** The database keeps a SHA-256 and a readable prefix, so a dump of
  this database authenticates nobody. The key exists exactly twice: in the response that issued it,
  and in the partner's secret manager.
- **Identity resolves on `(partner, subject)` only — never on email.** A unique index enforces it.
  If a token request could resolve a user by email address, anyone holding a key could reach an
  existing person's records by naming their address.
- **No refresh token ever reaches a browser.** Renewal is the partner's component re-calling the
  partner's own session-guarded route, so a user logging out of the partner app revokes our access
  for free.
- **Ten-minute tokens**, signed asymmetrically with a key persisted in Postgres, so a redeploy does
  not 401 every worker mid-form.
- **Scope is the boundary**, checked server-side on every endpoint.
- **Revocation is one click**: suspend the partner or rotate their key in the console. Either takes
  effect on the next request; tokens already minted die within minutes on their own.

This replaced an RFC 8693 signed-assertion exchange, at David's direction, to keep the partner's
side to one HTTP call with no crypto. The trade accepted: the key is a bearer secret, so it travels
on every token request and a leak of the partner's copy grants access to their workers until
rotated. What was kept: hashing at rest, show-once issuance, and instant revocation — the parts of
"harden later" that cost nothing now.

## Onboarding a partner

Sign in to `/admin/partners` and press **Register partner**. Give it a client id and a name.

Their API key is shown **once**, on the screen that follows. Nothing stores it — leaving the page
loses it, and the only way to issue another is **New key** on the partner (which kills the old one).
Copy it there and send it over a channel you trust.

Then send them:

- `docs/bas-for-mygigsters.html` — print to PDF, for the non-technical reader
- `docs/partner-integration.md` — for their engineers
- `scripts/partner-selftest.mjs` — proves the key works before they build anything

They run the self-test first — plain Node, no dependencies:

```bash
BAS_PARTNER_KEY=bas_... node partner-selftest.mjs
```

Every line should say PASS. It stops short of submitting, so nothing reaches the practice.

## Configuration

| Setting | Source |
| --- | --- |
| `ConnectionStrings__basdb` | Aspire (env in the container) |
| `PartnerAuth__Issuer` | env; this service's public origin |
| `Cors__AllowedOrigins__<n>` | env; the partner origins our JS is embedded on |

Local development:

```bash
cp src/Api/appsettings.Development.json.example src/Api/appsettings.Development.json
dotnet run --project src/AppHost
```

The AppHost brings up Postgres and applies migrations on startup.

## Tests

```bash
dotnet run --project tests/Api.Tests/Api.Tests.csproj
```

`dotnet run`, not `dotnet test`: xunit v3 runs on Microsoft.Testing.Platform, so the test project is
its own executable and the .NET 10 SDK will not drive it through the old VSTest target.

**Requires a running Docker daemon.** The suite runs against a real Postgres via Testcontainers,
which costs about ten seconds of container start. That's deliberate: an in-memory SQLite would
remove the dependency but only by giving up the two things this suite exists for — the real EF
migrations are applied exactly as a deploy applies them, and writes genuinely run in parallel, so
the unique index arbitrating concurrent provisioning is actually exercised rather than serialised
into a no-op.

The service's clock is `TimeProvider` throughout, including inside JWT lifetime validation, so
token expiry, retry backoff and webhook retention are tested by advancing a fake clock rather
than waiting.

## Deploying

`.github/workflows/ci.yml` runs the test suite on every push to `main` and every pull request, so
nothing lands unguarded. `.github/workflows/deploy.yml` (manual dispatch) runs the tests again,
publishes the contracts package, builds the image, generates the compose file with
`aspire publish`, and ships it to the same Lightsail box as `PracticeManager.Api`, behind the same
Caddy.

### Deploy PracticeManager.Api first

**This ordering is not optional.** The reconciler sends a blank `statementType`, meaning "find the
statement the ATO issued; do not create one". A `PracticeManager.Api` older than the
`find-only-activity-statement` change rejects that with `InvalidArgument`, which this service reads
as a rejection — so every push would spend its attempt budget and land in `failed` after eight
tries, on statements that were perfectly good.

The change is backward-compatible in the other direction: blank was previously rejected outright, so
deploying it early breaks nothing that works today.

### First deploy

1. Set the repository secrets: `POSTGRES_PASSWORD`, `DEPLOY_SSH_KEY`, `PRACTICEMANAGER_API_KEY`
   (the same value as `PracticeManager.Api`'s `SECURITY_API_KEY`), `ADMIN_API_KEY` for scripts
   (`openssl rand -base64 32`), and `ADMIN_INITIAL_PASSWORD` for the first admin account.

2. Set the repository variables: `SERVER_HOST`, `SERVER_USER`, `BAS_REMOTE_DIR`, and `ADMIN_EMAIL`
   (the first admin account's sign-in email).

3. The admin account is created on first boot from `ADMIN_EMAIL` / `ADMIN_INITIAL_PASSWORD` —
   nothing is committed to the repository. **Change the password after the first sign-in.**
   Seeding is create-only, so it never overwrites what you change.

4. Point `bas.nighttax.com.au` at the box. Caddy picks it up from the compose labels.

5. Register the partner from the console, or `POST /admin/v1/partners` with the `x-admin-key`
   header.

6. Confirm the service is up:

   ```bash
   curl -s https://bas.nighttax.com.au/health
   ```

### Operational invariants

**Exactly one replica of the API container.** Both background workers — the reconciler pushing
statements into Practice Manager and the webhook dispatcher — select due rows with a plain query.
There is no lease column and no `FOR UPDATE SKIP LOCKED`, so a second replica would pick up the
same rows and push the same statement into the live practice twice. `MigrateOnStartup` likewise
assumes a single migrator. This is the right trade for a single-box deployment; if scaling out is
ever actually needed, both workers need row leases (`FOR UPDATE SKIP LOCKED`), migrations need to
move to a one-shot job, and only then may `deploy.replicas` exceed 1.

### Back up Postgres

Nothing else holds this data. It is every worker's identity, their encrypted TFNs, and their lodged
figures — and git is explicitly not backing it up.

```bash
# On the box. Adjust the compose service name if it differs.
sudo docker compose exec -T bas-postgres   pg_dump -U bas -d basdb --format=custom   > "/var/backups/bas/basdb-$(date -u +%Y%m%d-%H%M).dump"
```

Worth a nightly cron and off-box copies. **The dump is plaintext**: worker TFNs and the
token-signing key are stored as written, so the dump file is both personal information under the
Privacy Act TFN Rule and a credential that can mint a token for any worker. Give it the same
protection as the database itself, and do not leave it on a shared box unencrypted.

## What is not here yet

Nothing further is planned from the original build order. Every partner request now carries
`partner_id`, `worker_id` and `jti` on its logging scope, so a line from inside a request traces
back to the token that authorised it and the mint that issued it.

**The read-back is not wired up here yet.** `PracticeManager.Api` now reads the statement back
inside the push and reports `netAmount`, the issued `statementType`, and the `has*` section flags on
`SyncActivityStatementResponse` — but those fields are additive and unpublished, so this service
still cannot see them. Once the contracts package ships, `PracticeManagerGateway` maps them onto
`PushOutcome.Pushed` and the reconciler copies them onto the period. Until then `netAmount` and
`StatementType` stay null.

`in_review` and `lodged` are modelled, documented and delivered by webhook, but nothing sets them
yet: they reflect the agent's own progress inside Practice Manager, and PM exposes no status field
for a statement that has been harvested and verified. That is the last piece of 3d, and it needs a
harvest against the live practice rather than a guess at a field name.

Two questions from the plan are still open, and both gate 3c:

**Who picks the statement type — resolved.** Nobody upstream of the ATO does. `statementType` is
gone from the partner request, and the reconciler sends it blank, which `PracticeManager.Api` now
reads as find-only. One half remains: `BasPeriod.StatementType` is still never populated, because
`SyncActivityStatementResponse` does not report the type of the statement it found. Reading it back
belongs with 3d, alongside the net amount.

**Which labels a statement actually carries.** PM publishes `hasGST`, `hasPAYGI`, `hasPAYGW`,
`hasFTC`, `hasWET`, `hasLCT` per statement, and none of it is exposed over gRPC yet. Until it is,
this service will send T and W figures to a statement that may have no such section. PM writes what
it can and reports which sections went, so nothing is corrupted — but a worker can send figures that
quietly go nowhere. Worth closing in 3d.

**Who captures the declaration.** Lodging a BAS is a legal act by the taxpayer. Either the partner
captures it and we rely on them contractually, or the agent's own declaration covers it.
