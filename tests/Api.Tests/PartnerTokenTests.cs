using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Bas.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The partner token endpoint, driven through the real HTTP pipeline.
///
/// <para>Everything downstream trusts what this endpoint decides, so a gap here is a gap in front
/// of real people's tax data. The properties worth most are the negatives: what a wrong key, a
/// suspended partner and a narrowed scope cannot do.</para>
/// </summary>
public sealed class PartnerTokenTests(BasApiFactory factory) : IClassFixture<BasApiFactory>, IDisposable
{
    private readonly BasApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task A_valid_key_mints_a_usable_token()
    {
        var apiKey = await _factory.GetPartnerApiKeyAsync();

        using var response = await BasApiFactory.RequestTokenAsync(_client, "worker-1", null, apiKey);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // A token is a credential; nothing on the way may cache the response.
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        var token = (await response.Content.ReadFromJsonAsync<PartnerTokenResponse>())!;
        token.TokenType.ShouldBe("Bearer");
        token.ExpiresIn.ShouldBe(600);
        token.Scope.ShouldBe("bas:read bas:write profile:write");

        var parsed = new JsonWebToken(token.AccessToken);
        parsed.Issuer.ShouldBe(BasApiFactory.Issuer);
        parsed.Audiences.ShouldContain(BasApiFactory.Audience);
        Guid.TryParse(parsed.Subject, out _).ShouldBeTrue("sub must be a Worker id this service minted");
        parsed.GetClaim(BasClaims.PartnerId).Value.ShouldBe(BasApiFactory.PartnerClientId);
    }

    [Fact]
    public async Task The_token_authenticates_against_the_protected_surface()
    {
        var token = await _factory.MintTokenAsync(_client, "worker-surface");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workers/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<WorkerIdentityResponse>();
        body!.PartnerId.ShouldBe(BasApiFactory.PartnerClientId);
        body.WorkerId.ShouldNotBe(Guid.Empty);
    }

    // -------------------------------------------------------------------- who is refused

    [Fact]
    public async Task No_key_is_refused()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/partner/token", new PartnerTokenRequest { Subject = "worker-1" });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<PartnerTokenError>())!
            .Error.ShouldBe(PartnerTokenErrors.InvalidKey);
    }

    [Fact]
    public async Task A_wrong_key_is_refused_with_the_same_answer_as_a_missing_partner()
    {
        // Make the partner exist first, then present a key with its real prefix shape but wrong
        // material - the closest an attacker gets, and it must fail exactly like nonsense does.
        await _factory.GetPartnerApiKeyAsync();

        using var response = await BasApiFactory.RequestTokenAsync(
            _client, "worker-1", null, "bas_" + new string('x', 43));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadFromJsonAsync<PartnerTokenError>())!
            .Error.ShouldBe(PartnerTokenErrors.InvalidKey);
    }

    [Fact]
    public async Task A_missing_subject_is_a_bad_request()
    {
        var apiKey = await _factory.GetPartnerApiKeyAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/partner/token")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add(PartnerTokens.HeaderName, apiKey);

        var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ------------------------------------------------------- identity and provisioning

    [Fact]
    public async Task The_same_subject_always_resolves_to_the_same_worker()
    {
        // That stability is how a worker's history survives from quarter to quarter.
        var first = await _factory.MintTokenAsync(_client, "worker-stable");
        var second = await _factory.MintTokenAsync(_client, "worker-stable");

        SubjectOf(first).ShouldBe(SubjectOf(second));

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        (await db.PartnerUserLinks.CountAsync(l => l.PartnerSub == "worker-stable")).ShouldBe(1);
    }

    [Fact]
    public async Task Different_subjects_resolve_to_different_workers()
    {
        var a = await _factory.MintTokenAsync(_client, "worker-a");
        var b = await _factory.MintTokenAsync(_client, "worker-b");

        SubjectOf(a).ShouldNotBe(SubjectOf(b));
    }

    [Fact]
    public async Task Concurrent_first_contact_for_one_subject_still_creates_one_worker()
    {
        // The unique index on (PartnerId, PartnerSub) is what decides this. If provisioning ever
        // stopped handling the losing insert, the symptom would be a duplicated identity rather
        // than an error - so it is worth pinning down.
        var apiKey = await _factory.GetPartnerApiKeyAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            BasApiFactory.RequestTokenAsync(_client, "worker-race", null, apiKey)));

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Dispose();
        }

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        (await db.PartnerUserLinks.CountAsync(l => l.PartnerSub == "worker-race")).ShouldBe(1);
    }

    // ---------------------------------------------------------------------------- scope

    [Fact]
    public async Task A_narrowed_token_is_narrowed_server_side()
    {
        var token = await _factory.MintTokenAsync(_client, "worker-narrow", scope: BasScopes.BasRead);
        token.Scope.ShouldBe(BasScopes.BasRead);

        // The narrowing has to bind on the server, or it is decoration.
        using var write = new HttpRequestMessage(HttpMethod.Put, "/api/v1/bas/2026/4")
        {
            Content = JsonContent.Create(new SaveBasRequest { TotalSales = 100 })
        };
        write.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        (await _client.SendAsync(write)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_unknown_scope_is_refused()
    {
        var apiKey = await _factory.GetPartnerApiKeyAsync();

        using var response = await BasApiFactory.RequestTokenAsync(
            _client, "worker-1", "admin:everything", apiKey);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<PartnerTokenError>())!
            .Error.ShouldBe(PartnerTokenErrors.InvalidScope);
    }

    private static string SubjectOf(PartnerTokenResponse token) =>
        new JsonWebToken(token.AccessToken).Subject;
}
