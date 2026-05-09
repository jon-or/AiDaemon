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
    readonly IDispatcher _dispatcher;

    Mutex? _instanceMutex;

    public Worker(
        ILogger<Worker> logger,
        IOptions<DaemonOptions> options,
        IHostApplicationLifetime lifetime,
        IStateStore stateStore,
        INotificationPoller poller,
        ITriagePipeline triage,
        IBranchResolver resolver,
        IDispatcher dispatcher)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
        _stateStore = stateStore;
        _poller = poller;
        _triage = triage;
        _resolver = resolver;
        _dispatcher = dispatcher;
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

        try
        {
            await _dispatcher.ReconcileOnStartupAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "startup reconciliation failed — continuing");
        }

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
                    try
                    {
                        await _dispatcher.SweepAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "sweep failed");
                    }

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

        // Branches we've already dispatched (or run agent triage for) this tick. A second
        // notification on the same branch within one poll gets logged + recorded but doesn't
        // re-fire the agent-triage / dispatch path. Cross-tick reuse is handled by the
        // branches table's RcActive state via the dispatcher.
        var dispatchedThisTick = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var n in _poller.PollAsync(cancellationToken))
        {
            seen++;
            var commentId = NotificationPoller.DeriveCommentId(n);

            _logger.LogInformation(
                "notification thread={ThreadId} repo={Repo} type={Type} reason={Reason} title={Title}",
                n.Id, n.Repository.FullName, n.Subject.Type, n.Reason, n.Subject.Title);

            // ---------- L1 + L2: cheap deterministic filters ----------
            TriageVerdict? quick;
            try
            {
                quick = await _triage.QuickTriageAsync(n, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "quick triage threw thread={ThreadId} — skipping", n.Id);
                continue;
            }

            if (quick is { Action: TriageAction.Drop })
            {
                dropped++;
                _logger.LogInformation(
                    "verdict thread={ThreadId} action=Drop why={Why} (L1/L2)",
                    n.Id, quick.Why);
                await _stateStore.MarkProcessedAsync(n.Id, commentId, $"dropped:{quick.Why}", cancellationToken);
                continue;
            }

            // ---------- Resolve branch (needed for agent triage AND dispatch) ----------
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
                await _stateStore.MarkProcessedAsync(n.Id, commentId, "unresolved", cancellationToken);
                continue;
            }

            _logger.LogInformation(
                "resolved thread={ThreadId} branch={Branch} worktree={Worktree} pr={Pr} issue={Issue}",
                n.Id, branch.Branch, branch.Worktree, branch.PrNumber, branch.IssueNumber);

            // ---------- Within-tick coalesce: only one agent run + dispatch per branch ----------
            if (!dispatchedThisTick.Add(branch.Key))
            {
                coalesced++;
                _logger.LogInformation(
                    "coalesced thread={ThreadId} branch={Branch} (already dispatched this tick)",
                    n.Id, branch.Key);
                await _stateStore.MarkProcessedAsync(n.Id, commentId, $"coalesced:{branch.Key}", cancellationToken);
                continue;
            }

            // ---------- L3: agent triage (and the actual research/fix work) in the worktree ----------
            TriageVerdict verdict;
            if (quick is { Action: TriageAction.Actionable })
            {
                // Quick triage produced a definitive actionable (e.g. a future shortcut). Treat
                // it as final, with no agent session. The dispatcher will fresh-spawn RC.
                verdict = quick;
            }
            else
            {
                try
                {
                    verdict = await _triage.AgentTriageAsync(n, branch, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "agent triage threw thread={ThreadId} branch={Branch}", n.Id, branch.Key);
                    await _stateStore.MarkProcessedAsync(n.Id, commentId, $"failed:agent-triage:{branch.Key}", cancellationToken);
                    continue;
                }
            }

            string outcome;
            if (verdict.Action == TriageAction.Drop)
            {
                dropped++;
                _logger.LogInformation(
                    "verdict thread={ThreadId} action=Drop why={Why} (L3)",
                    n.Id, verdict.Why);
                outcome = $"dropped:{verdict.Why}";
            }
            else
            {
                actionable++;
                _logger.LogInformation(
                    "verdict thread={ThreadId} action=Actionable summary={Summary} why={Why} confidence={Confidence:F2} sid={Sid}",
                    n.Id, verdict.Summary, verdict.Why, verdict.Confidence, verdict.SessionId ?? "(none)");

                DispatchOutcome dispatchOutcome;
                try
                {
                    dispatchOutcome = await _dispatcher.DispatchAsync(branch, n, verdict, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "dispatch threw thread={ThreadId} branch={Branch}", n.Id, branch.Key);
                    dispatchOutcome = DispatchOutcome.Failed;
                }

                outcome = dispatchOutcome switch
                {
                    DispatchOutcome.Spawned => $"spawned:{branch.Key}",
                    DispatchOutcome.HeadsUp => $"heads_up:{branch.Key}",
                    _ => $"failed:{branch.Key}",
                };
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
