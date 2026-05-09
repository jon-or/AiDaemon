using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Options;

namespace AiDaemon;

public class Worker : BackgroundService
{
    readonly ILogger<Worker> _logger;
    readonly IOptions<DaemonOptions> _options;
    readonly IHostApplicationLifetime _lifetime;
    readonly IStateStore _stateStore;
    readonly INotificationPoller _poller;
    readonly ITriagePipeline _triage;

    Mutex? _instanceMutex;

    public Worker(
        ILogger<Worker> logger,
        IOptions<DaemonOptions> options,
        IHostApplicationLifetime lifetime,
        IStateStore stateStore,
        INotificationPoller poller,
        ITriagePipeline triage)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
        _stateStore = stateStore;
        _poller = poller;
        _triage = triage;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _instanceMutex = new Mutex(true, @"Global\AiDaemon", out var owned);

        if (!owned)
        {
            _logger.LogCritical("Another instance of AiDaemon is already running. Exiting.");
            _lifetime.StopApplication();
            return;
        }

        await _stateStore.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;

        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;
        var pausedFile = Path.Combine(opts.DataDir, "PAUSED");
        var interval = TimeSpan.FromSeconds(Math.Max(1, opts.PollIntervalSeconds));

        _logger.LogInformation(
            "AiDaemon worker starting. PollInterval={IntervalSeconds}s DataDir={DataDir} AiUserLogin={AiUserLogin}",
            interval.TotalSeconds, opts.DataDir, opts.AiUserLogin);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(pausedFile))
                {
                    _logger.LogInformation("paused (delete {Path} to resume)", pausedFile);
                }
                else
                {
                    await TickAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "tick failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("AiDaemon worker stopping");
    }

    async Task TickAsync(CancellationToken cancellationToken)
    {
        var seen = 0;
        var dropped = 0;
        var actionable = 0;

        await foreach (var n in _poller.PollAsync(cancellationToken))
        {
            seen++;
            var commentId = NotificationPoller.DeriveCommentId(n);

            _logger.LogInformation(
                "notification thread={ThreadId} repo={Repo} type={Type} reason={Reason} title={Title}",
                n.Id, n.Repository.FullName, n.Subject.Type, n.Reason, n.Subject.Title);

            TriageVerdict verdict;
            try
            {
                verdict = await _triage.TriageAsync(n, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "triage threw thread={ThreadId} — skipping", n.Id);
                continue;
            }

            string outcome;
            if (verdict.Action == TriageAction.Drop)
            {
                dropped++;
                outcome = $"dropped:{verdict.Why}";
                _logger.LogInformation(
                    "verdict thread={ThreadId} action=Drop why={Why}",
                    n.Id, verdict.Why);
            }
            else
            {
                actionable++;
                outcome = "actionable";
                // Phase 4 will spawn an RC session here. For now, just log.
                _logger.LogInformation(
                    "verdict thread={ThreadId} action=Actionable summary={Summary} why={Why} confidence={Confidence:F2}",
                    n.Id, verdict.Summary, verdict.Why, verdict.Confidence);
            }

            await _stateStore.MarkProcessedAsync(n.Id, commentId, outcome, cancellationToken);
        }

        if (seen > 0)
            _logger.LogInformation(
                "tick seen={Seen} dropped={Dropped} actionable={Actionable}",
                seen, dropped, actionable);
        else
            _logger.LogDebug("tick (no new notifications)");
    }
}
