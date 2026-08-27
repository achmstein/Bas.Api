using Bas.Api.Data.Entities;
using Bas.Api.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bas.Api.Tests;

/// <summary>
/// A host whose Practice Manager gateway is under the test's control.
///
/// <para>The reconciler's background loop is switched off — the tests drive
/// <see cref="BasReconciler.SweepAsync"/> directly, so a sweep happens exactly when a test says so
/// rather than whenever a timer fires.</para>
/// </summary>
public class ReconcilerFactory : BasApiFactory
{
    public FakePracticeManager Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Reconciler:Enabled"] = "false",
                // Never dialled: the gateway below replaces the client entirely.
                ["PracticeManager:Endpoint"] = "http://practicemanager.invalid:8081"
            }));

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPracticeManagerGateway>();
            services.AddSingleton<IPracticeManagerGateway>(Gateway);
        });
    }
}

/// <summary>A Practice Manager that answers whatever the test tells it to.</summary>
public sealed class FakePracticeManager : IPracticeManagerGateway
{
    private int _calls;

    /// <summary>What the next push returns.</summary>
    public PushOutcome Outcome { get; set; } = new PushOutcome.Pushed(1, 1, ["Gst"]);

    /// <summary>How many pushes have been attempted. Proves what was, and was not, sent.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The statement of the most recent push.</summary>
    public BasPeriod? LastPeriod { get; private set; }

    /// <summary>The TFN of the most recent push, so a test can prove it was decrypted correctly.</summary>
    public string? LastTfn { get; private set; }

    public Task<PushOutcome> PushAsync(
        BasPeriod period, Worker worker, string tfn, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        LastPeriod = period;
        LastTfn = tfn;

        return Task.FromResult(Outcome);
    }
}
