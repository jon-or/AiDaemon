using AiDaemon.Configuration;
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

    Mutex? _instanceMutex;

    public Worker(
        ILogger<Worker> logger,
        IOptions<DaemonOptions> options,
        IHostApplicationLifetime lifetime,
        IStateStore stateStore,
        INotificationPoller poller)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
        _stateStore = stateStore;
        _poller = poller;
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
            "AiDaemon worker starting. PollInterval={IntervalSeconds}s DataDir={DataDir} GhConfigDir={GhConfigDir}",
            interval.TotalSeconds, opts.DataDir, opts.GhConfigDir);

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
        var fresh = 0;

        await foreach (var n in _poller.PollAsync(cancellationToken))
        {
            seen++;
            var commentId = NotificationPoller.DeriveCommentId(n);

            _logger.LogInformation(
                "notification thread={ThreadId} repo={Repo} type={Type} reason={Reason} title={Title}",
                n.Id, n.Repository.FullName, n.Subject.Type, n.Reason, n.Subject.Title);

            // Phase 1: just record we saw it. Triage + dispatch land in Phase 2/4.
            await _stateStore.MarkProcessedAsync(n.Id, commentId, "seen", cancellationToken);
            fresh++;
        }

        if (seen > 0)
            _logger.LogInformation("tick processed={Fresh} new of {Seen} unread", fresh, seen);
        else
            _logger.LogDebug("tick (no unread notifications)");
    }
}
