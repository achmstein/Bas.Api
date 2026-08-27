using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Bas.Api.Admin;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Shouldly;

namespace Bas.Api.Tests;

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
    public async Task A_wrong_admin_key_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        request.Headers.Add(AdminAuthenticationHandler.HeaderName, "not-the-key");

        (await _client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_partner_access_token_cannot_reach_the_admin_surface()
    {
        // The single most important assertion in this file. A partner holding bas:write must not
        // be one claim away from suspending a competitor - admin is a separate authentication
        // scheme, so a bearer token cannot satisfy it at all rather than merely failing a check
        // somebody has to remember to write.
        var token = await _factory.MintTokenAsync(_client, "admin-isolation");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_partner_api_key_cannot_reach_the_admin_surface_either()
    {
        // Same property from the other direction: the credential we issue a partner must not open
        // the door that manages partners.
        var apiKey = await _factory.GetPartnerApiKeyAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        request.Headers.Add(AdminAuthenticationHandler.HeaderName, apiKey);

        (await _client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------------------- issuing the key

    [Fact]
    public async Task Registering_returns_the_api_key_once_and_it_works()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        var created = await scoped.RegisterPartnerAsync("issue-check", "Issue check");

        created.ApiKey.ShouldStartWith(PartnerTokens.KeyPrefix);
        created.ApiKey.Length.ShouldBeGreaterThanOrEqualTo(40);
        created.Partner.ApiKeyPrefix.ShouldBe(created.ApiKey[..12]);

        // The key it hands over has to actually authenticate, or the partner finds out it does not.
        using var minted = await BasApiFactory.RequestTokenAsync(client, "issued-worker", null, created.ApiKey);
        minted.StatusCode.ShouldBe(HttpStatusCode.OK);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task The_key_is_never_stored_or_shown_again()
    {
        // The whole protection: the database keeps a hash, so a dump authenticates nobody. If any
        // later read could return the key, that would not be true.
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        var created = await scoped.RegisterPartnerAsync("never-stored", "Never stored");
        var secretPart = created.ApiKey[PartnerTokens.KeyPrefix.Length..];

        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners/never-stored");
        read.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        var body = await (await client.SendAsync(read)).Content.ReadAsStringAsync();

        body.ShouldNotContain(secretPart);
        body.ShouldContain(created.Partner.ApiKeyPrefix!);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task Rotating_the_key_refuses_the_old_one_immediately()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        var oldKey = await scoped.GetPartnerApiKeyAsync();
        (await BasApiFactory.RequestTokenAsync(client, "rotate-1", null, oldKey))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var rotate = new HttpRequestMessage(
            HttpMethod.Put, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/key");
        rotate.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);

        var response = await client.SendAsync(rotate);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var rotated = (await response.Content.ReadFromJsonAsync<CreatePartnerResult>())!;
        rotated.ApiKey.ShouldNotBe(oldKey);

        // The old key is dead on the next request - which is the point, for a suspected leak.
        (await BasApiFactory.RequestTokenAsync(client, "rotate-2", null, oldKey))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await BasApiFactory.RequestTokenAsync(client, "rotate-3", null, rotated.ApiKey))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await scoped.DisposeAsync();
    }

    // --------------------------------------------------------------------- the kill switch

    [Fact]
    public async Task Suspending_a_partner_stops_token_minting_immediately()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        var apiKey = await scoped.GetPartnerApiKeyAsync();
        (await BasApiFactory.RequestTokenAsync(client, "kill-1", null, apiKey))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using var suspend = new HttpRequestMessage(
            HttpMethod.Post, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/suspend")
        {
            Content = JsonContent.Create(new SuspendRequest { Reason = "testing the kill switch" })
        };
        suspend.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        (await client.SendAsync(suspend)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Refused without a restart or a redeploy - and with the same answer as a wrong key, so a
        // caller cannot tell suspension apart from revocation.
        (await BasApiFactory.RequestTokenAsync(client, "kill-2", null, apiKey))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await scoped.DisposeAsync();
    }

    // ------------------------------------------------------------------------ what it shows

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

    [Fact]
    public async Task Retrying_a_lodgement_returns_the_lodgement_it_retried()
    {
        // The response used to be fished back out of the recent-200 list, which returned a 200
        // with a null body once the period fell outside it. A mutation answers with its row.
        var periodId = await SubmitStatementAsync("admin-retry-response");

        var response = await Admin().PostAsync($"/admin/v1/lodgements/{periodId}/retry", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var row = await response.Content.ReadFromJsonAsync<AdminLodgementResponse>();
        row.ShouldNotBeNull();
        row.PeriodId.ShouldBe(periodId);
        row.Status.ShouldBe(BasStatuses.Submitted);
        row.AttemptCount.ShouldBe(0);
    }

    // --------------------------------------------------------------------------- webhooks

    [Fact]
    public async Task Setting_a_webhook_issues_a_secret_once()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();
        await scoped.GetPartnerApiKeyAsync();

        var first = await SetWebhookAsync(client, "https://partner.test/hooks", newSecret: false);
        first.Secret.ShouldNotBeNullOrWhiteSpace("a URL with no secret must issue one");
        first.Partner.HasWebhookSecret.ShouldBeTrue();

        // Changing the address alone must NOT rotate the secret - that would silently break every
        // signature the partner verifies, with nothing to explain it.
        var moved = await SetWebhookAsync(client, "https://partner.test/hooks/v2", newSecret: false);
        moved.Secret.ShouldBeNull();
        moved.Partner.WebhookUrl.ShouldBe("https://partner.test/hooks/v2");

        var rotated = await SetWebhookAsync(client, "https://partner.test/hooks/v2", newSecret: true);
        rotated.Secret.ShouldNotBeNullOrWhiteSpace();
        rotated.Secret.ShouldNotBe(first.Secret);

        // And the secret is never readable afterwards.
        using var read = new HttpRequestMessage(
            HttpMethod.Get, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}");
        read.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        var body = await (await client.SendAsync(read)).Content.ReadAsStringAsync();
        body.ShouldNotContain(rotated.Secret!);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task Clearing_the_webhook_retires_its_secret()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();
        await scoped.GetPartnerApiKeyAsync();

        await SetWebhookAsync(client, "https://partner.test/hooks", newSecret: false);
        var cleared = await SetWebhookAsync(client, null, newSecret: false);

        cleared.Partner.WebhookUrl.ShouldBeNull();
        // A secret for an endpoint that no longer exists is just something else to leak.
        cleared.Partner.HasWebhookSecret.ShouldBeFalse();

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task A_webhook_url_must_be_https()
    {
        await _factory.GetPartnerApiKeyAsync();

        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/webhook")
        {
            Content = JsonContent.Create(new SetWebhookRequest { Url = "http://partner.test/hooks" })
        };
        request.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);

        (await _client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // --------------------------------------------------------------------------------- audit

    [Fact]
    public async Task Every_change_is_audited_against_the_key_that_made_it()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();
        await scoped.GetPartnerApiKeyAsync();

        using var suspend = new HttpRequestMessage(
            HttpMethod.Post, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/suspend")
        {
            Content = JsonContent.Create(new SuspendRequest { Reason = "audited" })
        };
        suspend.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        await client.SendAsync(suspend);

        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/audit");
        read.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        var entries = (await (await client.SendAsync(read)).Content
            .ReadFromJsonAsync<List<AuditEntryResponse>>())!;

        entries.Select(e => e.Action).ShouldContain("partner.created");
        var suspended = entries.Single(e => e.Action == "partner.suspended");
        suspended.Subject.ShouldBe(BasApiFactory.PartnerClientId);
        suspended.Detail.ShouldBe("audited");

        // Named keys, so an entry says which caller rather than "someone".
        entries.ShouldAllBe(e => e.Actor == BasApiFactory.AdminKeyName);

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task Reads_are_not_audited()
    {
        // Every partner-facing request already logs its partner and token id. Recording every
        // admin GET as well would bury the handful of entries that actually matter.
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        using var read = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/partners");
        read.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        await client.SendAsync(read);

        using var audit = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/audit");
        audit.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        var entries = await (await client.SendAsync(audit)).Content
            .ReadFromJsonAsync<List<AuditEntryResponse>>();

        entries.ShouldBeEmpty();

        await scoped.DisposeAsync();
    }

    [Fact]
    public async Task The_audit_records_key_prefixes_and_never_key_material()
    {
        using var scoped = new AdminFactory();
        await scoped.InitializeAsync();
        using var client = scoped.CreateClient();

        var created = await scoped.RegisterPartnerAsync("audit-prefix", "Audit prefix");

        using var audit = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/audit");
        audit.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        var body = await (await client.SendAsync(audit)).Content.ReadAsStringAsync();

        body.ShouldContain(created.Partner.ApiKeyPrefix!);
        body.ShouldNotContain(created.ApiKey[PartnerTokens.KeyPrefix.Length..]);

        await scoped.DisposeAsync();
    }

    // ------------------------------------------------------------------------------- the UI

    [Fact]
    public async Task The_console_sends_a_signed_out_visitor_to_the_login_page()
    {
        // Never a JSON 401: an operator whose cookie expired mid-task needs somewhere to go.
        foreach (var path in new[] { "/admin", "/admin/lodgements", "/admin/partners", "/admin/audit" })
        {
            var response = await _client.GetAsync(path);

            response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized, $"{path} answered a JSON 401");
            response.Content.Headers.ContentType?.MediaType.ShouldNotBe("application/problem+json");
        }

        (await _client.GetAsync("/admin/login")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_sign_in_form_posts_the_way_a_browser_posts_it()
    {
        // Submits EVERY hidden field the page rendered, duplicates included, because that is what a
        // browser does. An earlier version picked the first match of each name and sailed past a
        // form that rendered the antiforgery token twice - the browser posted both, and every
        // sign-in answered a bare HTTP 400.
        var page = await _client.GetStringAsync("/admin/login");

        var hidden = Regex.Matches(page, "<input[^>]*type=\"hidden\"[^>]*>")
            .Select(m => m.Value)
            .Select(tag => (
                Name: Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value,
                Value: Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value))
            .Where(f => f.Name.Length > 0)
            .ToList();

        hidden.Count(f => f.Name == "__RequestVerificationToken")
            .ShouldBe(1, "the form rendered more than one antiforgery token");

        var body = hidden
            .Select(f => (KeyValuePair<string, string>)new(f.Name, f.Value))
            .Append(new KeyValuePair<string, string>("Input.Email", "nobody@example.com"))
            .Append(new KeyValuePair<string, string>("Input.Password", "not-the-password"));

        var response = await _client.PostAsync("/admin/login", new FormUrlEncodedContent(body));

        // Re-rendering "those details were not accepted" is the pass; a 400 means antiforgery
        // rejected the page's own token.
        response.StatusCode.ShouldNotBe(HttpStatusCode.BadRequest, "antiforgery rejected its own token");
    }

    // ------------------------------------------------------------------------------ helpers

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);
        return client;
    }

    private static async Task<WebhookResult> SetWebhookAsync(HttpClient client, string? url, bool newSecret)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"/admin/v1/partners/{BasApiFactory.PartnerClientId}/webhook")
        {
            Content = JsonContent.Create(new SetWebhookRequest { Url = url, NewSecret = newSecret })
        };
        request.Headers.Add(AdminAuthenticationHandler.HeaderName, BasApiFactory.AdminKey);

        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<WebhookResult>())!;
    }

    private async Task<Guid> SubmitStatementAsync(string subject)
    {
        var token = await _factory.MintTokenAsync(_client, subject);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

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
