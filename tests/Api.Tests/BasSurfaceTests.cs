using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bas.Api.Contracts.Bas;
using Bas.Api.Contracts.Partner;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// The activity-statement surface, driven through the real pipeline with a real token.
///
/// <para>The clock is fixed at 27 August 2026, which is inside FY2027 Q1 — so Q1 is in progress and
/// FY2026 Q4 (Apr–Jun 2026) is the most recent finished quarter. Tests that submit use a finished
/// quarter; the one that proves you cannot lodge early uses the current one.</para>
/// </summary>
public sealed class BasSurfaceTests(BasApiFactory factory) : IClassFixture<BasApiFactory>, IDisposable
{
    private const int FinishedYear = 2026;
    private const int FinishedQuarter = 4;
    private const int CurrentYear = 2027;
    private const int CurrentQuarter = 1;

    private readonly BasApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    // ------------------------------------------------------------------- worker identity

    [Fact]
    public async Task Worker_starts_incomplete_and_becomes_complete_once_identity_is_saved()
    {
        var client = await AuthenticatedAsync("worker-identity");

        var before = await client.GetFromJsonAsync<WorkerIdentityResponse>("/api/v1/workers/me");
        before!.IsCompleteForLodgement.ShouldBeFalse();
        before.TfnMasked.ShouldBeNull();

        var response = await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await response.Content.ReadFromJsonAsync<WorkerIdentityResponse>();
        after!.IsCompleteForLodgement.ShouldBeTrue();
        after.FirstName.ShouldBe("Jordan");
    }

    [Fact]
    public async Task The_TFN_is_never_returned_in_full()
    {
        // Practice Manager already holds the real one. A screen that shows it is a screen that ends
        // up in a screenshot.
        var client = await AuthenticatedAsync("worker-tfn-masked");

        var response = await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldNotContain("123456782");
        (await response.Content.ReadFromJsonAsync<WorkerIdentityResponse>())!.TfnMasked.ShouldBe("******782");
    }

    [Fact]
    public async Task An_invalid_TFN_is_refused_without_being_echoed_back()
    {
        var client = await AuthenticatedAsync("worker-bad-tfn");

        var response = await client.PutAsJsonAsync(
            "/api/v1/workers/me", ValidIdentity() with { Tfn = "123456789" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("checksum");
        body.ShouldNotContain("123456789");
    }

    [Fact]
    public async Task An_invalid_ABN_is_refused()
    {
        var client = await AuthenticatedAsync("worker-bad-abn");

        var response = await client.PutAsJsonAsync(
            "/api/v1/workers/me", ValidIdentity() with { Abn = "51824753557" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Saving_identity_needs_the_profile_scope()
    {
        var client = await AuthenticatedAsync("worker-scope", scope: BasScopes.BasRead);

        var response = await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------------- reading periods

    [Fact]
    public async Task An_untouched_quarter_comes_back_as_an_empty_draft()
    {
        var client = await AuthenticatedAsync("bas-empty");

        var period = await client.GetFromJsonAsync<BasPeriodResponse>(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}");

        period!.Status.ShouldBe(BasStatuses.Draft);
        period.Id.ShouldBe(Guid.Empty);
        period.TotalSales.ShouldBeNull();
        period.PeriodStart.ShouldBe(new DateOnly(2026, 4, 1));
        period.PeriodEnd.ShouldBe(new DateOnly(2026, 6, 30));
        period.DueDate.ShouldBe(new DateOnly(2026, 7, 28));
    }

    [Fact]
    public async Task The_list_shows_recent_quarters_even_before_anything_is_saved()
    {
        var client = await AuthenticatedAsync("bas-list");

        var periods = await client.GetFromJsonAsync<List<BasPeriodSummary>>("/api/v1/bas");

        periods.ShouldNotBeEmpty();
        // Newest first, and never a quarter that has not finished.
        periods![0].FinancialYear.ShouldBe(FinishedYear);
        periods[0].Quarter.ShouldBe(FinishedQuarter);
        periods.ShouldAllBe(p => p.PeriodEnd < new DateOnly(2026, 8, 27));
    }

    [Theory]
    [InlineData(2027, 0)]
    [InlineData(2027, 5)]
    [InlineData(1990, 1)]
    public async Task An_impossible_period_is_a_bad_request(int year, int quarter)
    {
        var client = await AuthenticatedAsync($"bas-bad-{year}-{quarter}");

        var response = await client.GetAsync($"/api/v1/bas/{year}/{quarter}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------------- saving figures

    [Fact]
    public async Task Saving_figures_creates_the_statement_and_reads_back()
    {
        var client = await AuthenticatedAsync("bas-save");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var saved = await response.Content.ReadFromJsonAsync<BasPeriodResponse>();
        saved!.Id.ShouldNotBe(Guid.Empty);
        saved.TotalSales.ShouldBe(31900);
        saved.GstOnSales.ShouldBe(2900);
        saved.GstOnPurchases.ShouldBe(870);
        saved.Status.ShouldBe(BasStatuses.Draft);

        // The ATO issues the statement and chooses its type. Nothing upstream of the ATO gets to
        // assert one, so this stays null until the reconciler finds the real statement.
        saved.StatementType.ShouldBeNull();

        // Net amount is Practice Manager's to compute, so it stays null until the push reads it back.
        saved.NetAmount.ShouldBeNull();
    }

    [Fact]
    public async Task A_worker_with_no_instalment_obligation_keeps_a_null_T_section()
    {
        // Not zero. A statement with a T section of zero is a different statement from one the ATO
        // issued without a T section at all.
        var client = await AuthenticatedAsync("bas-null-t");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        var saved = await response.Content.ReadFromJsonAsync<BasPeriodResponse>();
        saved!.InstalmentIncome.ShouldBeNull();
        saved.AtoInstalmentAmount.ShouldBeNull();
        saved.TotalSalaryWages.ShouldBeNull();
    }

    [Fact]
    public async Task Saving_replaces_rather_than_merges()
    {
        // Documented on SaveBasRequest and enforced here: an absent label means the statement has
        // no such label. A partner sending only what changed would be surprised, so the behaviour
        // is pinned rather than left to inference.
        var client = await AuthenticatedAsync("bas-replace");

        await client.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        var second = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}",
            new SaveBasRequest { GstOnSales = 2900 });

        var saved = await second.Content.ReadFromJsonAsync<BasPeriodResponse>();
        saved!.GstOnSales.ShouldBe(2900);
        saved.TotalSales.ShouldBeNull();
    }

    [Fact]
    public async Task A_varied_instalment_without_a_reason_code_is_refused()
    {
        // T4 is what tells the ATO why T9 was varied down; without it the statement bounces.
        var client = await AuthenticatedAsync("bas-t9");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}",
            SimplerBas() with { VariedInstalmentAmount = 400 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_negative_amount_is_refused()
    {
        var client = await AuthenticatedAsync("bas-negative");

        var response = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas() with { TotalSales = -1 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Saving_needs_the_write_scope()
    {
        var client = await AuthenticatedAsync("bas-read-only", scope: BasScopes.BasRead);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // --------------------------------------------------------------------------- submitting

    [Fact]
    public async Task Submitting_queues_the_statement_and_answers_202()
    {
        var client = await AuthenticatedAsync("bas-submit");
        await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        await client.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        var response = await client.PostAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);

        // 202, not 200: the practice has not seen it yet.
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var body = await response.Content.ReadFromJsonAsync<SubmitBasResponse>();
        body!.Status.ShouldBe(BasStatuses.Submitted);
        body.PeriodId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Submitting_twice_returns_the_original_acknowledgement()
    {
        // A partner whose response was lost should be able to retry without a conflict.
        var client = await AuthenticatedAsync("bas-resubmit");
        await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        await client.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        var first = await client.PostAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);
        var second = await client.PostAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);

        second.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var a = await first.Content.ReadFromJsonAsync<SubmitBasResponse>();
        var b = await second.Content.ReadFromJsonAsync<SubmitBasResponse>();
        b!.PeriodId.ShouldBe(a!.PeriodId);
        b.SubmittedAt.ShouldBe(a.SubmittedAt);
    }

    [Fact]
    public async Task A_submitted_statement_can_no_longer_be_edited()
    {
        // Otherwise the agent reviews one set of numbers while the worker believes another was sent.
        var client = await AuthenticatedAsync("bas-locked");
        await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        await client.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());
        await client.PostAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);

        var response = await client.PutAsJsonAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas() with { TotalSales = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Submitting_without_a_complete_worker_identity_is_refused()
    {
        // Checked here rather than at the push: the reconciler would otherwise retry forever, and
        // each attempt that gets as far as creating a client orphans one in the live practice.
        var client = await AuthenticatedAsync("bas-no-identity");
        await client.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        var response = await client.PostAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("workers/me");
    }

    [Fact]
    public async Task A_quarter_that_has_not_ended_cannot_be_submitted()
    {
        var client = await AuthenticatedAsync("bas-too-early");
        await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        await client.PutAsJsonAsync($"/api/v1/bas/{CurrentYear}/{CurrentQuarter}", SimplerBas());

        var response = await client.PostAsync($"/api/v1/bas/{CurrentYear}/{CurrentQuarter}/submit", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).ShouldContain("2026-09-30");
    }

    [Fact]
    public async Task Submitting_an_empty_statement_is_refused()
    {
        var client = await AuthenticatedAsync("bas-empty-submit");
        await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());

        var response = await client.PostAsync(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------------------ status

    [Fact]
    public async Task Status_reports_the_due_date_even_for_an_untouched_quarter()
    {
        var client = await AuthenticatedAsync("bas-status-empty");

        var status = await client.GetFromJsonAsync<BasStatusResponse>(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/status");

        status!.Status.ShouldBe(BasStatuses.Draft);
        status.DueDate.ShouldBe(new DateOnly(2026, 7, 28));
        status.NetAmount.ShouldBeNull();
    }

    [Fact]
    public async Task Status_follows_the_statement_through_submission()
    {
        var client = await AuthenticatedAsync("bas-status-flow");
        await client.PutAsJsonAsync("/api/v1/workers/me", ValidIdentity());
        await client.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());
        await client.PostAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/submit", null);

        var status = await client.GetFromJsonAsync<BasStatusResponse>(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}/status");

        status!.Status.ShouldBe(BasStatuses.Submitted);
        status.SubmittedAt.ShouldNotBeNull();
    }

    // ------------------------------------------------------------------------- isolation

    [Fact]
    public async Task One_worker_cannot_see_another_workers_figures()
    {
        // There is no route that names a worker - the subject comes from the token - so this is
        // really a check that the token's subject is what scopes the query.
        var mine = await AuthenticatedAsync("bas-isolation-a");
        var theirs = await AuthenticatedAsync("bas-isolation-b");

        await mine.PutAsJsonAsync($"/api/v1/bas/{FinishedYear}/{FinishedQuarter}", SimplerBas());

        var seen = await theirs.GetFromJsonAsync<BasPeriodResponse>(
            $"/api/v1/bas/{FinishedYear}/{FinishedQuarter}");

        seen!.TotalSales.ShouldBeNull();
        seen.Id.ShouldBe(Guid.Empty);
    }

    // ------------------------------------------------------------------------------ helpers

    private static WorkerIdentityRequest ValidIdentity() => new()
    {
        Tfn = "123456782",
        Abn = "51824753556",
        FirstName = "Jordan",
        FamilyName = "Ellis",
        DateOfBirth = new DateOnly(1994, 3, 12),
        Email = "jordan@example.com",
        Phone = "0400 000 000"
    };

    private static SaveBasRequest SimplerBas() => new()
    {
        TotalSales = 31900,
        GstOnSales = 2900,
        GstOnPurchases = 870,
        TotalPurchases = 9570,
        CashAccountingMethod = true
    };

    /// <summary>A client carrying a real token for a worker of its own.</summary>
    private async Task<HttpClient> AuthenticatedAsync(string subject, string? scope = null)
    {
        var now = _factory.Clock.GetUtcNow();

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

        using var response = await _client.PostAsync("/api/v1/partner/token", new FormUrlEncodedContent(form));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var token = (await response.Content.ReadFromJsonAsync<TokenExchangeResponse>())!;

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}
