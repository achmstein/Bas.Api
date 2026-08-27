using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

// Dashboard host ports already taken on the shared server (each maps to container 18888):
//   18886, 18887 taxtron, 18888 coinpay, 18889 ato, 18890 knowem, 18900 practicemanager.
// 18910 leaves room above the practicemanager stack.
builder.AddDockerComposeEnvironment("bas")
    .WithDashboard(dashboard => dashboard.WithHostPort(18910))
    .ConfigureComposeFile(compose =>
    {
        compose.AddNetwork(new Network
        {
            Name = "caddy",
            External = true
        });
    });

// PracticeManager.Api runs in its own compose stack on the same box, reachable over the shared
// `caddy` network. Native gRPC lives on its 8081 listener - 8080 is HTTP/1.1 only, because Kestrel
// cannot multiplex h2c and HTTP/1.1 on one cleartext endpoint.
var practiceManagerEndpoint = builder.AddParameter(
    "practicemanager-endpoint", value: "http://practicemanager-api:8081");
var practiceManagerApiKey = builder.AddParameter("practicemanager-api-key", secret: true, value: "");

// A key for scripts, plus the first admin account. Nothing is committed: the account is seeded
// from these values on first boot (create-only — rotate the password through the UI afterwards).
var adminApiKey = builder.AddParameter("admin-api-key", secret: true, value: "");
var adminEmail = builder.AddParameter("admin-email", value: "");
var adminInitialPassword = builder.AddParameter("admin-initial-password", secret: true, value: "");

var postgres = builder.AddPostgres("bas-postgres")
    // Aspire generates a password per publish otherwise, which would be a new password — and so a
    // locked-out database — on every deploy.
    .WithPassword(builder.AddParameter("postgres-password", secret: true))
    .WithDataVolume("bas-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("basdb");

// HARD INVARIANT: exactly one replica of bas-api. Both sweep workers (the reconciler and the
// webhook dispatcher) select due rows with a plain query — no lease, no FOR UPDATE SKIP LOCKED —
// and MigrateOnStartup assumes a single migrator. A second replica would push the same statement
// into the live practice twice. Scaling out requires row leases in both workers and a separate
// migration step first; see "Operational invariants" in the README.
builder.AddProject<Projects.Api>("bas-api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("PracticeManager__Endpoint", practiceManagerEndpoint)
    .WithEnvironment("PracticeManager__ApiKey", practiceManagerApiKey)
    .WithEnvironment("Admin__Keys__0__Name", "deploy-runbook")
    .WithEnvironment("Admin__Keys__0__Key", adminApiKey)
    .WithEnvironment("Admin__Users__0__Email", adminEmail)
    .WithEnvironment("Admin__Users__0__InitialPassword", adminInitialPassword)
    // REST only — unlike PracticeManager.Api there is no native gRPC listener to keep off the
    // HTTP/1.1 endpoint, so one cleartext port behind Caddy is the whole story.
    .WithEnvironment("Kestrel__Endpoints__http__Url", "http://*:8080")
    .WithEnvironment("Kestrel__Endpoints__http__Protocols", "Http1")
    .WithEndpoint("http", e =>
    {
        e.TargetPort = 8080;
        e.UriScheme = "http";
        // Kestrel binds 8080 itself via the env vars above; Aspire's dev proxy would need
        // Port ≠ TargetPort, and the appsettings-derived endpoint already pins Port=8080.
        e.IsProxied = false;
    })
    .WithHttpHealthCheck("/health")
    .PublishAsDockerFile()
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Restart = "unless-stopped";
        service.Ports.Clear();
        service.Networks.Add("caddy");
        service.Labels["caddy"] = "bas.nighttax.com.au";
        service.Labels["caddy.reverse_proxy"] = "{{upstreams 8080}}";
    });

builder.Build().Run();
