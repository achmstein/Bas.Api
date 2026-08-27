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
/// End-to-end coverage of the token exchange, driven through the real HTTP pipeline.
///
/// <para>These are the tests that matter most in this service: everything downstream of the token
/// endpoint trusts whatever it decides, so a gap here is a gap in front of real people's tax data.</para>
/// </summary>
public sealed class TokenExchangeTests(BasApiFactory factory) : IClassFixture<BasApiFactory>, IDisposable
{
    // One host and one Postgres container for the whole class. Starting a container per test cost
    // six minutes and bought nothing: the tests below isolate themselves by using a distinct
    // partner subject each, which is the same thing real traffic does.
    private readonly BasApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task Valid_exchange_returns_a_usable_access_token()
    {
        var response = await ExchangeAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var token = await response.Content.ReadFromJsonAsync<TokenExchangeResponse>();
        token.ShouldNotBeNull();
        token.TokenType.ShouldBe("Bearer");
        token.IssuedTokenType.ShouldBe(TokenExchange.AccessTokenType);
        token.ExpiresIn.ShouldBe(600);
        token.Scope.ShouldBe("bas:read bas:write profile:write");

        var parsed = new JsonWebToken(token.AccessToken);
        parsed.Issuer.ShouldBe(BasApiFactory.Issuer);
        parsed.Audiences.ShouldContain(BasApiFactory.Audience);
        Guid.TryParse(parsed.Subject, out _).ShouldBeTrue("sub must be a Worker id this service minted");
        parsed.GetClaim(BasClaims.PartnerId).Value.ShouldBe(BasApiFactory.PartnerClientId);
    }

    [Fact]
    public async Task Token_response_is_not_cacheable()
    {
        // It carries a credential; an intermediary that stored one would hand it to the next caller.
        var response = await ExchangeAsync();

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Issued_token_authenticates_against_the_protected_surface()
    {
        var token = await ExchangeForTokenAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/workers/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<WorkerIdentityResponse>();
        body.ShouldNotBeNull();
        body.PartnerId.ShouldBe(BasApiFactory.PartnerClientId);
        body.WorkerId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Protected_surface_refuses_an_unauthenticated_caller()
    {
        var response = await _client.GetAsync("/api/v1/workers/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ------------------------------------------------------- identity and provisioning

    [Fact]
    public async Task Same_partner_subject_resolves_to_the_same_worker()
    {
        const string subject = "worker-repeat";

        var first = await ExchangeForTokenAsync(subject);
        var second = await ExchangeForTokenAsync(subject);

        SubjectOf(first).ShouldBe(SubjectOf(second));

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        (await db.PartnerUserLinks.CountAsync(l => l.PartnerSub == subject)).ShouldBe(1);
    }

    [Fact]
    public async Task Different_partner_subjects_resolve_to_different_workers()
    {
        var first = await ExchangeForTokenAsync("worker-distinct-a");
        var second = await ExchangeForTokenAsync("worker-distinct-b");

        SubjectOf(first).ShouldNotBe(SubjectOf(second));
    }

    [Fact]
    public async Task Concurrent_first_contact_for_one_subject_still_creates_one_worker()
    {
        // The unique index on (PartnerId, PartnerSub) is what decides this. If provisioning ever
        // stopped handling the losing insert, the symptom would be a worker with a duplicate
        // identity rather than an error — so it is worth pinning down.
        var now = _factory.Clock.GetUtcNow();

        var exchanges = Enumerable.Range(0, 8)
            .Select(_ => PostAsync(BuildForm(now, subject: "worker-race")))
            .ToArray();

        var responses = await Task.WhenAll(exchanges);

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Dispose();
        }

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();
        (await db.PartnerUserLinks.CountAsync(l => l.PartnerSub == "worker-race")).ShouldBe(1);
    }

    // ------------------------------------------------------------------- client checks

    [Fact]
    public async Task Unknown_client_is_refused_without_revealing_that_it_is_unknown()
    {
        using var stranger = new PartnerSigner("not-registered", BasApiFactory.Issuer);
        var now = _factory.Clock.GetUtcNow();

        var response = await PostAsync(new Dictionary<string, string>
        {
            [TokenExchange.Fields.GrantType] = TokenExchange.GrantType,
            [TokenExchange.Fields.ClientAssertionType] = TokenExchange.ClientAssertionType,
            [TokenExchange.Fields.ClientAssertion] = stranger.CreateClientAssertion(now),
            [TokenExchange.Fields.SubjectTokenType] = TokenExchange.SubjectTokenType,
            [TokenExchange.Fields.SubjectToken] = stranger.CreateSubjectToken("worker-1", now)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var error = await response.Content.ReadFromJsonAsync<TokenErrorResponse>();
        error!.Error.ShouldBe(TokenErrors.InvalidClient);
        // Identical to the wrong-signature case: whether a client_id exists is not enumerable.
        error.ErrorDescription.ShouldBe("Client authentication failed.");
    }

    [Fact]
    public async Task Assertion_signed_by_the_wrong_key_is_refused()
    {
        // An impostor claiming to be the registered partner, signing with a key of their own.
        using var impostor = new PartnerSigner(BasApiFactory.PartnerClientId, BasApiFactory.Issuer);
        var now = _factory.Clock.GetUtcNow();

        var response = await PostAsync(new Dictionary<string, string>
        {
            [TokenExchange.Fields.GrantType] = TokenExchange.GrantType,
            [TokenExchange.Fields.ClientAssertionType] = TokenExchange.ClientAssertionType,
            [TokenExchange.Fields.ClientAssertion] = impostor.CreateClientAssertion(now),
            [TokenExchange.Fields.SubjectTokenType] = TokenExchange.SubjectTokenType,
            [TokenExchange.Fields.SubjectToken] = impostor.CreateSubjectToken("worker-1", now)
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Assertion_for_the_wrong_audience_is_refused()
    {
        // Otherwise an assertion the partner minted for some other service could be relayed here.
        var now = _factory.Clock.GetUtcNow();
        var form = BuildForm(now);
        form[TokenExchange.Fields.ClientAssertion] =
            _factory.Partner.CreateClientAssertion(now, audience: "https://someone-else.example");

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Expired_assertion_is_refused()
    {
        var now = _factory.Clock.GetUtcNow();
        var form = BuildForm(now);

        _factory.Clock.Advance(TimeSpan.FromMinutes(10));

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Assertion_with_an_over_long_lifetime_is_refused()
    {
        // A partner minting day-long assertions would quietly turn one captured assertion into a
        // day of impersonation. The ceiling is ours to enforce, not theirs.
        var now = _factory.Clock.GetUtcNow();
        var form = BuildForm(now);
        form[TokenExchange.Fields.ClientAssertion] =
            _factory.Partner.CreateClientAssertion(now, lifetime: TimeSpan.FromHours(24));

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Replayed_assertion_is_refused()
    {
        var now = _factory.Clock.GetUtcNow();
        var form = BuildForm(now);

        (await PostAsync(form)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PostAsync(form)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Client_assertion_whose_subject_is_not_the_client_id_is_refused()
    {
        // RFC 7523 §3: for client authentication the assertion is about the client itself.
        var now = _factory.Clock.GetUtcNow();
        var form = BuildForm(now);
        form[TokenExchange.Fields.ClientAssertion] =
            _factory.Partner.CreateClientAssertion(now, subject: "somebody-else");

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Subject_token_is_refused_when_it_carries_no_subject()
    {
        var now = _factory.Clock.GetUtcNow();
        var form = BuildForm(now);
        form[TokenExchange.Fields.SubjectToken] = _factory.Partner.CreateSubjectToken(string.Empty, now);

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<TokenErrorResponse>())!.Error.ShouldBe(TokenErrors.InvalidGrant);
    }

    // ------------------------------------------------------------------------- protocol

    [Theory]
    [InlineData(TokenExchange.Fields.GrantType, "authorization_code", TokenErrors.UnsupportedGrantType)]
    [InlineData(TokenExchange.Fields.ClientAssertionType, "something-else", TokenErrors.InvalidRequest)]
    [InlineData(TokenExchange.Fields.SubjectTokenType, "something-else", TokenErrors.InvalidRequest)]
    public async Task Wrong_protocol_constants_are_rejected(string field, string value, string expectedError)
    {
        var form = BuildForm(_factory.Clock.GetUtcNow());
        form[field] = value;

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<TokenErrorResponse>())!.Error.ShouldBe(expectedError);
    }

    [Fact]
    public async Task Non_form_request_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/partner/token", new { grant_type = "whatever" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<TokenErrorResponse>())!.Error.ShouldBe(TokenErrors.InvalidRequest);
    }

    // ---------------------------------------------------------------------------- scope

    [Fact]
    public async Task Requesting_a_subset_of_granted_scopes_narrows_the_token()
    {
        var token = await ExchangeForTokenAsync(scope: BasScopes.BasRead);

        token.Scope.ShouldBe(BasScopes.BasRead);
        new JsonWebToken(token.AccessToken).GetClaim(BasClaims.Scope).Value.ShouldBe(BasScopes.BasRead);
    }

    [Fact]
    public async Task Unknown_scope_is_rejected()
    {
        var form = BuildForm(_factory.Clock.GetUtcNow());
        form[TokenExchange.Fields.Scope] = "admin:everything";

        var response = await PostAsync(form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<TokenErrorResponse>())!.Error.ShouldBe(TokenErrors.InvalidScope);
    }

    // --------------------------------------------------------------------------- helpers

    private static string SubjectOf(TokenExchangeResponse token) => new JsonWebToken(token.AccessToken).Subject;

    private Dictionary<string, string> BuildForm(
        DateTimeOffset now, string subject = "worker-default", string? scope = null)
    {
        var form = new Dictionary<string, string>
        {
            [TokenExchange.Fields.GrantType] = TokenExchange.GrantType,
            [TokenExchange.Fields.ClientAssertionType] = TokenExchange.ClientAssertionType,
            [TokenExchange.Fields.ClientAssertion] = _factory.Partner.CreateClientAssertion(now),
            [TokenExchange.Fields.SubjectTokenType] = TokenExchange.SubjectTokenType,
            [TokenExchange.Fields.SubjectToken] = _factory.Partner.CreateSubjectToken(subject, now)
        };

        if (scope is not null)
            form[TokenExchange.Fields.Scope] = scope;

        return form;
    }

    private Task<HttpResponseMessage> PostAsync(Dictionary<string, string> form) =>
        _client.PostAsync("/api/v1/partner/token", new FormUrlEncodedContent(form));

    private Task<HttpResponseMessage> ExchangeAsync(string subject = "worker-default", string? scope = null) =>
        PostAsync(BuildForm(_factory.Clock.GetUtcNow(), subject, scope));

    private async Task<TokenExchangeResponse> ExchangeForTokenAsync(
        string subject = "worker-default", string? scope = null)
    {
        using var response = await ExchangeAsync(subject, scope);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<TokenExchangeResponse>())!;
    }
}
