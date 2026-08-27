using System.Diagnostics.Metrics;

namespace Bas.Api.Infrastructure;

/// <summary>
/// The handful of numbers that say whether the pipeline is moving. This system's characteristic
/// failure is a queue silently stopping — nothing throws, nothing pages, statements just sit — so
/// the queue's depth and age are published rather than discovered by a human opening the console.
///
/// <para>The gauges read fields the reconciler refreshes each sweep; observable callbacks never
/// touch the database.</para>
/// </summary>
public sealed class BasMetrics : IDisposable
{
    public const string MeterName = "Bas.Api";

    private readonly Meter _meter;
    private readonly Counter<long> _pushFailures;
    private readonly Counter<long> _webhooksAbandoned;

    private long _pendingStatements;
    private double _oldestPendingAgeSeconds;

    public BasMetrics(IMeterFactory meterFactory)
    {
        _meter = meterFactory.Create(MeterName);

        _pushFailures = _meter.CreateCounter<long>(
            "bas.push.failures",
            description: "Practice Manager pushes that did not land, by outcome " +
                         "(rejected, unavailable, failed).");

        _webhooksAbandoned = _meter.CreateCounter<long>(
            "bas.webhooks.abandoned",
            description: "Deliveries given up on. The partner can still poll.");

        _meter.CreateObservableGauge(
            "bas.sync.pending",
            () => Interlocked.Read(ref _pendingStatements),
            description: "Statements waiting to be pushed, as of the last reconciler sweep.");

        _meter.CreateObservableGauge(
            "bas.sync.oldest_pending_age",
            () => Volatile.Read(ref _oldestPendingAgeSeconds),
            unit: "s",
            description: "How long the most overdue pending statement has been waiting. " +
                         "A number that only grows means the queue has stopped.");
    }

    /// <summary>Exposed so a test can tell this instance's instruments from another host's.</summary>
    internal Meter Meter => _meter;

    public void PushRejected() => _pushFailures.Add(1, new KeyValuePair<string, object?>("outcome", "rejected"));

    public void PushUnavailable() => _pushFailures.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable"));

    /// <summary>A terminal failure: budget exhausted, or the statement can never be pushed.</summary>
    public void PushFailed() => _pushFailures.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));

    public void WebhookAbandoned() => _webhooksAbandoned.Add(1);

    /// <summary>Called by the reconciler once per sweep with the queue's current shape.</summary>
    public void RecordSyncQueue(int pending, TimeSpan oldestPendingAge)
    {
        Interlocked.Exchange(ref _pendingStatements, pending);
        Volatile.Write(ref _oldestPendingAgeSeconds, Math.Max(0, oldestPendingAge.TotalSeconds));
    }

    public void Dispose() => _meter.Dispose();
}
