using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Bas.Api.Admin;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>A host with an admin key configured.</summary>
public sealed class AdminFactory : ReconcilerFactory
{
    public const string AdminKey = "a-test-admin-key-that-is-long-enough";
    public const string AdminKeyName = "test-runbook";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Admin:Keys:0:Name"] = AdminKeyName,
                ["Admin:Keys:0:Key"] = AdminKey
            }));
    }
}

/// <summary>
/// The admin surface.
///
/// <para>The tests that matter most here are the negative ones. This surface can suspend a partner
/// and read what every worker has lodged, so "who cannot reach it" is a more important property
/// than any feature it offers.</para>
/// </summary>
public sealed class AdminTests(AdminFactory factory) : IClassFixture<AdminFactory>, IDisposable
{
    private readonly AdminFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    // -------------------------------------------------------------------------- who gets in

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        (await _client.GetAsync("/admin/v1/partners")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_key_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        request.Headers.Add(AdminAuthenticationHandler.HeaderName, "not-the-key");

        (await _client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_partner_access_token_cannot_reach_the_admin_surface()
    {
        // The single most important assertion in this file. A partner holding bas:write must not be
        // one claim away from suspending a competitor - so admin is a separate authentication
        // scheme, and a bearer token cannot satisfy it at all rather than merely failing a check
        // somebody has to remember to write.
        var token = await PartnerTokenAsync("admin-isolation");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_valid_key_gets_in()
    {
        var partners = await Admin().GetFromJsonAsync<List<AdminPartnerResponse>>("/admin/v1/partners");

        partners.ShouldNotBeNull();
        partners.ShouldContain(p => p.ClientId == BasApiFactory.PartnerClientId);
    }

    // ------------------------------------------------------------------------ what it shows

    [Fact]
    public async Task A_partner_is_shown_without_anything_secret()
    {
        var response = await Admin().GetAsync($"/admin/v1/partners/{BasApiFactory.PartnerClientId}");
        var body = await response.Content.ReadAsStringAsync();

        // A fingerprint is enough to confirm a rotation took; the key itself would just make the
        // page unreadable, and the webhook secret must never leave the database.
        body.ShouldNotContain("BEGIN PUBLIC KEY");
        body.ShouldNotContain(WebhookFactory.WebhookSecret);

        var partner = await response.Content.ReadFromJsonAsync<AdminPartnerResponse>();
        partner!.PublicKeyFingerprint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Lodgements_never_carry_a_TFN()
    {
        await SubmitStatementAsync("admin-lodgement");

        var body = await Admin().GetStringAsync("/admin/v1/lodgements");

        body.ShouldNotContain("123456782");
        body.ShouldNotContain("tfn", Case.Insensitive);
    }

    [Fact]
    public async Task Lodgements_carry_the_ledgers_view_of_why_something_is_stuck()
    {
        var periodId = await SubmitStatementAsync("admin-stuck");

        var rows = await Admin().GetFromJsonAsync<List<AdminLodgementResponse>>("/admin/v1/lodgements");
        var row = rows!.Single(r => r.PeriodId == periodId);

        row.PartnerId.ShouldBe(BasApiFactory.PartnerClientId);
        row.PartnerSub.ShouldBe("admin-stuck");
        row.SyncStatus.ShouldBe("Pending");
    }

    // --------------------------------------------------------------------- the kill switch

    [Fact]
    public async Task Suspending_a_partner_stops_token_exchange_immediately()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        // Works before.
        (await ExchangeAsync(scoped, client, "kill-switch")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var suspend = new HttpRequestMessage(
            HttpMethod.Post, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/suspend")
        {
            Content = JsonContent.Create(new SuspendRequest { Reason = "testing the kill switch" })
        };
        suspend.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);

        (await client.SendAsync(suspend)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // And refused after, without a restart or a redeploy.
        var after = await ExchangeAsync(scoped, client, "kill-switch-2");
        after.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task Rotating_the_key_refuses_assertions_signed_with_the_old_one()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        using var replacement = new PartnerSigner(BasApiFactory.PartnerClientId, BasApiFactory.Issuer);

        using var rotate = new HttpRequestMessage(
            HttpMethod.Put, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/key")
        {
            Content = JsonContent.Create(new RotateKeyRequest { PublicKeyPem = replacement.PublicKeyPem })
        };
        rotate.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);

        (await client.SendAsync(rotate)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The old key is dead on the next request - which is the point, for a suspected leak.
        (await ExchangeAsync(scoped, client, "rotated-old")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And the new one works.
        var now = scoped.Clock.GetUtcNow();
        var form = new Dictionary<string, string>
        {
            [TokenExchange.Fields.GrantType] = TokenExchange.GrantType,
            [TokenExchange.Fields.ClientAssertionType] = TokenExchange.ClientAssertionType,
            [TokenExchange.Fields.ClientAssertion] = replacement.CreateClientAssertion(now),
            [TokenExchange.Fields.SubjectTokenType] = TokenExchange.SubjectTokenType,
            [TokenExchange.Fields.SubjectToken] = replacement.CreateSubjectToken("rotated-new", now)
        };

        var response = await client.PostAsync("/api/v1/partner/token", new FormUrlEncodedContent(form));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task An_invalid_key_is_refused_at_rotation_rather_than_at_the_partners_next_call()
    {
        using var rsa = RSA.Create(2048);

        using var rotate = new HttpRequestMessage(
            HttpMethod.Put, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/key")
        {
            // Their private half. Accepting it would leave us holding their signing key.
            Content = JsonContent.Create(new RotateKeyRequest { PublicKeyPem = rsa.ExportPkcs8PrivateKeyPem() })
        };
        rotate.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);

        (await _client.SendAsync(rotate)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // --------------------------------------------------------------------------------- audit

    [Fact]
    public async Task Every_change_is_audited_against_the_key_that_made_it()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        using var suspend = new HttpRequestMessage(
            HttpMethod.Post, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/suspend")
        {
            Content = JsonContent.Create(new SuspendRequest { Reason = "audited" })
        };
        suspend.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);
        await client.SendAsync(suspend);

        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/audit");
        read.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);

        var entries = await (await client.SendAsync(read)).Content
            .ReadFromJsonAsync<List<AuditEntryResponse>>();

        var entry = entries!.ShouldHaveSingleItem();
        entry.Action.ShouldBe("partner.suspended");
        entry.Subject.ShouldBe(BasApiFactory.PartnerClientId);
        entry.Detail.ShouldBe("audited");

        // Named keys, so an entry says which caller rather than "someone".
        entry.Actor.ShouldBe(AdminFactory.AdminKeyName);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task Reads_are_not_audited()
    {
        // Every partner-facing request already logs its partner and token id. Recording every admin
        // GET as well would bury the handful of entries that actually matter.
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        read.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);
        await client.SendAsync(read);

        using var audit = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/audit");
        audit.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);

        var entries = await (await client.SendAsync(audit)).Content
            .ReadFromJsonAsync<List<AuditEntryResponse>>();

        entries.ShouldBeEmpty();

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task A_key_rotation_records_fingerprints_and_not_key_material()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        using var replacement = new PartnerSigner(BasApiFactory.PartnerClientId, BasApiFactory.Issuer);

        using var rotate = new HttpRequestMessage(
            HttpMethod.Put, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/key")
        {
            Content = JsonContent.Create(new RotateKeyRequest { PublicKeyPem = replacement.PublicKeyPem })
        };
        rotate.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);
        await client.SendAsync(rotate);

        using var audit = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/audit");
        audit.Headers.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);
        var body = await (await client.SendAsync(audit)).Content.ReadAsStringAsync();

        body.ShouldContain("partner.key_rotated");
        body.ShouldNotContain("BEGIN PUBLIC KEY");

        await scoped.DisposeAsync();
    }

    // ------------------------------------------------------------------------------- the UI

    [Fact]
    public async Task The_console_sends_a_signed_out_visitor_to_the_login_page()
    {
        using var client = _factory.CreateClient(
            new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/admin/lodgements");

        // Not a bare 403: an operator whose cookie expired mid-task needs somewhere to go.
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Found, HttpStatusCode.OK);
        (await client.GetAsync("/admin/login")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------------------ helpers

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminAuthenticationHandler.HeaderName, AdminFactory.AdminKey);
        return client;
    }

    private static Task<HttpResponseMessage> ExchangeAsync(BasApiFactory factory, HttpClient client, string subject)
    {
        var now = factory.Clock.GetUtcNow();

        return client.PostAsync("/api/v1/partner/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            [TokenExchange.Fields.GrantType] = TokenExchange.GrantType,
            [TokenExchange.Fields.ClientAssertionType] = TokenExchange.ClientAssertionType,
            [TokenExchange.Fields.ClientAssertion] = factory.Partner.CreateClientAssertion(now),
            [TokenExchange.Fields.SubjectTokenType] = TokenExchange.SubjectTokenType,
            [TokenExchange.Fields.SubjectToken] = factory.Partner.CreateSubjectToken(subject, now)
        }));
    }

    private async Task<string> PartnerTokenAsync(string subject)
    {
        using var response = await ExchangeAsync(_factory, _client, subject);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<TokenExchangeResponse>())!.AccessToken;
    }

    private async Task<Guid> SubmitStatementAsync(string subject)
    {
        var token = await PartnerTokenAsync(subject);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await client.PutAsJsonAsync("/api/v1/workers/me", new WorkerIdentityRequest
        {
            Tfn = "123456782",
            FirstName = "Jordan",
            FamilyName = "Ellis",
            DateOfBirth = new DateOnly(1994, 3, 12)
        });

        await client.PutAsJsonAsync("/api/v1/bas/2026/4", new SaveBasRequest
        {
            TotalSales = 31900, GstOnSales = 2900, GstOnPurchases = 870
        });

        var submit = await client.PostAsync("/api/v1/bas/2026/4/submit", null);
        submit.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        return (await submit.Content.ReadFromJsonAsync<SubmitBasResponse>())!.PeriodId;
    }
}
