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

// Protects signing-key private material at rest, and worker TFNs from phase 3b. Losing it means
// every stored signing key becomes undecryptable — it belongs in the deployment secret store, and
// it must outlive any single container.
var dataEncryptionKey = builder.AddParameter("data-encryption-key", secret: true);

var postgres = builder.AddPostgres("bas-postgres")
    // Aspire generates a password per publish otherwise, which would be a new password — and so a
    // locked-out database — on every deploy.
    .WithPassword(builder.AddParameter("postgres-password", secret: true))
    .WithDataVolume("bas-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("basdb");

builder.AddProject<Projects.Api>("bas-api")
    .WithReference(database)
    .WaitFor(database)
    .WithEnvironment("Security__DataEncryptionKey", dataEncryptionKey)
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
