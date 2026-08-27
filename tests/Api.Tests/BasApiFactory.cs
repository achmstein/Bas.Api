using Bas.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Bas.Api.Tests;

/// <summary>
/// Hosts the real application against a real Postgres: real routing, real authentication
/// middleware, real authorization policies, real startup reconciliation, and the actual EF
/// migrations rather than a schema conjured from the model.
///
/// <para>Requires a running Docker daemon. That is a deliberate cost. An in-memory SQLite would
/// remove it, but only by giving up the two things this suite most needs — migrations are applied
/// here exactly as a deploy applies them, and writes genuinely run in parallel, so the unique index
/// that arbitrates concurrent provisioning is actually exercised.</para>
/// </summary>
public class BasApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Issuer = "https://bas.test";
    public const string Audience = "bas-api";
    public const string PartnerClientId = "mygigsters-test";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("basdb")
        .WithUsername("bas")
        .WithPassword("bas")
        .Build();

    /// <summary>The partner this host is configured to trust.</summary>
    public PartnerSigner Partner { get; } = new(PartnerClientId, Issuer);

    /// <summary>Controllable clock, so expiry tests do not depend on real waiting.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

    /// <summary>Scopes granted to <see cref="Partner"/>. Set before the first request.</summary>
    public string AllowedScopes { get; set; } = "bas:read bas:write profile:write";

    /// <summary>Whether <see cref="Partner"/> is active. Set before the first request.</summary>
    public bool PartnerActive { get; set; } = true;

    /// <summary>xunit runs this once for the class fixture, before the first test.</summary>
    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        // Force the host up now, so a startup failure surfaces here rather than as a puzzling
        // failure inside whichever test happened to run first.
        _ = Services;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        Partner.Dispose();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:basdb"] = _postgres.GetConnectionString(),
            ["PartnerAuth:Issuer"] = Issuer,
            ["PartnerAuth:Audience"] = Audience,

            // The real migrations, applied the way a deploy applies them.
            ["Database:MigrateOnStartup"] = "true",

            ["PracticeManager:Endpoint"] = "http://practicemanager.invalid:8081",
            ["Reconciler:Enabled"] = "false",
            // No admin account in the test host: the seeded default would be created on every
            // fixture, and nothing here signs in through the console.
            ["Admin:Users:0:Email"] = "",

            // Registered through configuration, exercising the same reconciliation path a deploy uses.
            ["Partners:Registrations:0:ClientId"] = PartnerClientId,
            ["Partners:Registrations:0:Name"] = "MyGigsters (test)",
            ["Partners:Registrations:0:PublicKeyPem"] = Partner.PublicKeyPem,
            ["Partners:Registrations:0:AllowedScopes"] = AllowedScopes,
            ["Partners:Registrations:0:Active"] = PartnerActive.ToString()
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>A scope for arranging and asserting directly against the database.</summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}
