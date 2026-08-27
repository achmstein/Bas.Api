namespace Bas.Api.Infrastructure;

/// <summary>
/// A worker that sweeps for due rows once per tick — the shape both the reconciler and the webhook
/// dispatcher share. A sweep that throws is logged and retried at the next tick rather than taking
/// the host down.
///
/// <para>Deliberately thin: the due-row query, the scoping strategy and the options classes stay
/// with each worker, because those genuinely differ. Only the loop and its failure isolation are
/// worth writing once.</para>
/// </summary>
public abstract class SweepingBackgroundService(TimeProvider timeProvider, ILogger logger)
    : BackgroundService
{
    protected TimeProvider TimeProvider => timeProvider;

    protected ILogger Logger => logger;

    protected abstract bool Enabled { get; }

    protected abstract TimeSpan PollInterval { get; }

    /// <summary>Logged once when starting disabled. Say what the fallback behaviour is.</summary>
    protected abstract string DisabledMessage { get; }

    /// <summary>One pass over the due work. Public so a test can drive it without a timer.</summary>
    public abstract Task SweepAsync(CancellationToken cancellationToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            logger.LogInformation("{Worker}: {Reason}", GetType().Name, DisabledMessage);
            return;
        }

        logger.LogInformation("{Worker} started; polling every {Interval}.", GetType().Name, PollInterval);

        using var timer = new PeriodicTimer(PollInterval, timeProvider);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);

                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that throws must not take the host down - the next tick tries again.
                logger.LogError(ex, "{Worker} sweep failed; retrying at the next interval.", GetType().Name);
            }
        }
    }
}
