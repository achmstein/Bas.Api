using Bas.Api.Data;
using Bas.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Bas.Api.Tests;

/// <summary>
/// Invariants the schema itself enforces. Three call sites assume one partner link per worker;
/// this pins the constraint that makes the assumption safe, so a future second-link code path
/// fails loudly at insert rather than quietly double-notifying a partner.
/// </summary>
public sealed class DataInvariantTests(BasApiFactory factory) : IClassFixture<BasApiFactory>, IDisposable
{
    private readonly BasApiFactory _factory = factory;
    private readonly HttpClient _client = factory.CreateClient();

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task A_worker_cannot_carry_a_second_partner_link()
    {
        // Provisioning through the real token exchange creates the worker and its one link.
        await _factory.MintTokenAsync(_client, "invariant-single-link");

        await using var scope = _factory.CreateDbScope();
        var db = scope.ServiceProvider.GetRequiredService<BasDbContext>();

        var existing = await db.PartnerUserLinks
            .AsNoTracking()
            .SingleAsync(l => l.PartnerSub == "invariant-single-link");

        db.PartnerUserLinks.Add(new PartnerUserLink
        {
            PartnerId = existing.PartnerId,
            PartnerSub = "invariant-single-link-second",
            WorkerId = existing.WorkerId,
            CreatedAt = _factory.Clock.GetUtcNow(),
            LastSeenAt = _factory.Clock.GetUtcNow()
        });

        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
