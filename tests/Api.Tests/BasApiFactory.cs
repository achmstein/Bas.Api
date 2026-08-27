using System.Net.Http.Json;
using Bas.Api.Admin;
using Bas.Api.Contracts.Partner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace Bas.Api.Tests;

/// <summary>
/// Hosts the real application against a real Postgres: real routing, real authentication
/// middleware, real authorization policies, and the actual EF migrations rather than a schema
/// conjured from the model.
///
/// <para>Partners are registered through the real admin API rather than seeded around it, so every
/// test's partner exists the same way a production partner does — including the API key, which is
/// issued once and never stored.</para>
///
/// <para>Requires a running Docker daemon. That is a deliberate cost: an in-memory SQLite would
/// remove it, but only by skipping the migrations entirely and serialising the concurrency tests
/// into no-ops.</para>
/// </summary>
public class BasApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string Issuer = "https://bas.test";
    public const string Audience = "bas-api";
    public const string PartnerClientId = "mygigsters-test";

    public const string AdminKey = "a-test-admin-key-that-is-long-enough";
    public const string AdminKeyName = "test-runbook";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("basdb")
        .WithUsername("bas")
        .WithPassword("bas")
        .Build();

    private readonly SemaphoreSlim _partnerGate = new(1, 1);
    private string? _partnerApiKey;

    /// <summary>Controllable clock, so expiry tests do not depend on real waiting.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        _partnerGate.Dispose();
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

            // No admin account: nothing here signs in through the console, and the seeded default
            // would otherwise be created on every fixture. The named key is the admin credential.
            ["Admin:Users:0:Email"] = "",
            ["Admin:Keys:0:Name"] = AdminKeyName,
            ["Admin:Keys:0:Key"] = AdminKey,

            ["PracticeManager:Endpoint"] = "http://practicemanager.invalid:8081",
            ["Reconciler:Enabled"] = "false",
            ["Webhooks:Enabled"] = "false"
        }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    /// <summary>
    /// The test partner's API key, registering the partner on first use — through the real admin
    /// endpoint, so the key arrives exactly the way a production key does.
    /// </summary>
    public async Task<string> GetPartnerApiKeyAsync()
    {
        if (_partnerApiKey is not null)
            return _partnerApiKey;

        await _partnerGate.WaitAsync();
        try
        {
            if (_partnerApiKey is not null)
                return _partnerApiKey;

            var created = await RegisterPartnerAsync(PartnerClientId, "MyGigsters (test)");
            _partnerApiKey = created.ApiKey;
            return _partnerApiKey;
        }
        finally
        {
            _partnerGate.Release();
        }
    }

    /// <summary>Registers a partner through the real admin endpoint.</summary>
    public async Task<CreatePartnerResult> RegisterPartnerAsync(
        string clientId, string name, string scopes = "bas:read bas:write profile:write")
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/v1/partners")
        {
            Content = JsonContent.Create(new CreatePartnerRequest
            {
                ClientId = clientId,
                Name = name,
                AllowedScopes = scopes
            })
        };
        request.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminKey);

        using var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Registering partner '{clientId}' failed: HTTP {(int)response.StatusCode} " +
                $"{await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<CreatePartnerResult>())!;
    }

    /// <summary>Mints a worker token the way a partner's server does.</summary>
    public async Task<PartnerTokenResponse> MintTokenAsync(
        HttpClient client, string subject, string? scope = null, string? apiKey = null)
    {
        apiKey ??= await GetPartnerApiKeyAsync();

        using var response = await RequestTokenAsync(client, subject, scope, apiKey);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Token mint for '{subject}' failed: HTTP {(int)response.StatusCode} " +
                $"{await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PartnerTokenResponse>())!;
    }

    /// <summary>The raw token request, for tests asserting on refusals.</summary>
    public static Task<HttpResponseMessage> RequestTokenAsync(
        HttpClient client, string subject, string? scope, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/token")
        {
            Content = JsonContent.Create(new PartnerTokenRequest { Subject = subject, Scope = scope })
        };
        request.Headers.Add(PartnerTokens.HeaderName, apiKey);

        return client.SendAsync(request);
    }

    /// <summary>A scope for arranging and asserting directly against the database.</summary>
    public AsyncServiceScope CreateDbScope() => Services.CreateAsyncScope();
}

/// <summary>Kept as distinct types so each test class gets its own container and database.</summary>
public class ReconcilerFactory : BasApiFactory
{
    public FakePracticeManager Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<Bas.Api.Sync.IPracticeManagerGateway>();
            services.AddSingleton<Bas.Api.Sync.IPracticeManagerGateway>(Gateway);
        });
    }
}

/// <summary>An admin-focused host. Identical configuration; separate database.</summary>
public sealed class AdminFactory : ReconcilerFactory;
