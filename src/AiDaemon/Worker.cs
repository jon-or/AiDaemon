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
    readonly IBranchResolver _resolver;

    Mutex? _instanceMutex;

    public Worker(
        ILogger<Worker> logger,
        IOptions<DaemonOptions> options,
        IHostApplicationLifetime lifetime,
        IStateStore stateStore,
        INotificationPoller poller,
        ITriagePipeline triage,
        IBranchResolver resolver)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
        _stateStore = stateStore;
        _poller = poller;
        _triage = triage;
        _resolver = resolver;
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
        var coalesced = 0;

        // Branches we've already dispatched (or would dispatch in Phase 4) this tick. A second
        // notification on the same branch within one poll gets logged + recorded but doesn't
        // re-fire the spawn / heads-up path. Cross-tick reuse is handled separately by the
        // branches table's RcActive state in Phase 4.
        var dispatchedThisTick = new HashSet<string>(StringComparer.Ordinal);

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
                _logger.LogInformation(
                    "verdict thread={ThreadId} action=Actionable summary={Summary} why={Why} confidence={Confidence:F2}",
                    n.Id, verdict.Summary, verdict.Why, verdict.Confidence);

                BranchInfo? branch = null;
                try
                {
                    branch = await _resolver.ResolveAsync(n, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "branch resolve threw thread={ThreadId}", n.Id);
                }

                if (branch == null)
                {
                    outcome = "actionable:unresolved";
                }
                else if (!dispatchedThisTick.Add(branch.Key))
                {
                    coalesced++;
                    outcome = $"actionable:coalesced:{branch.Key}";
                    _logger.LogInformation(
                        "coalesced thread={ThreadId} branch={Branch} (already dispatched this tick)",
                        n.Id, branch.Key);
                }
                else
                {
                    outcome = $"actionable:{branch.Key}";
                    _logger.LogInformation(
                        "resolved thread={ThreadId} branch={Branch} worktree={Worktree} pr={Pr} issue={Issue}",
                        n.Id, branch.Branch, branch.Worktree, branch.PrNumber, branch.IssueNumber);
                    // Phase 4 will dispatch an RC session here.
                }
            }

            await _stateStore.MarkProcessedAsync(n.Id, commentId, outcome, cancellationToken);
        }

        if (seen > 0)
            _logger.LogInformation(
                "tick seen={Seen} dropped={Dropped} actionable={Actionable} coalesced={Coalesced} branches={Branches}",
                seen, dropped, actionable, coalesced, dispatchedThisTick.Count);
        else
            _logger.LogDebug("tick (no new notifications)");
    }
}
