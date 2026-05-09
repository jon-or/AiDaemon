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
        bool owned;
        try
        {
            _instanceMutex = new Mutex(true, @"Global\AiDaemon", out owned);
        }
        catch (AbandonedMutexException)
        {
            // A prior instance died without releasing. .NET marks the mutex acquired by us;
            // proceed as if we got it cleanly.
            _logger.LogWarning("recovered abandoned Global\\AiDaemon mutex from a prior crashed instance");
            _instanceMutex = new Mutex(true, @"Global\AiDaemon", out owned);
        }

        if (!owned)
        {
            // We don't own the handle, so we must not retain it — StopAsync would call
            // ReleaseMutex and throw ApplicationException.
            _instanceMutex.Dispose();
            _instanceMutex = null;
            _logger.LogCritical("Another instance of AiDaemon is already running. Exiting.");
            _lifetime.StopApplication();
            return;
        }

        await _stateStore.InitializeAsync(cancellationToken);

        try
        {
            await _dispatcher.ReconcileOnStartupAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "startup reconciliation failed — continuing");
        }

        await base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Release the mutex AFTER the host has signalled stoppingToken and waited for
        // ExecuteAsync to unwind. Releasing earlier would let a fast-restarting second
        // instance acquire the mutex during our shutdown grace period — two daemons
        // running concurrently against the same state store and worktrees.
        await base.StopAsync(cancellationToken);

        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
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

        // ============================================================================
        // Pass 1: poll every notification, run cheap L1+L2 filters, resolve to a branch,
        // group survivors by branch.Key. Notifications that drop or fail to resolve are
        // marked here and never reach pass 2.
        // ============================================================================
        var byBranch = new Dictionary<string, BranchBatch>(StringComparer.Ordinal);

        await foreach (var n in _poller.PollAsync(cancellationToken))
        {
            seen++;
            var commentId = NotificationPoller.DeriveCommentId(n);

            _logger.LogInformation(
                "notification thread={ThreadId} repo={Repo} type={Type} reason={Reason} title={Title}",
                n.Id, n.Repository.FullName, n.Subject.Type, n.Reason, n.Subject.Title);

            // L1+L2. Returns a verdict + the comment body we fetched (so pass 2 doesn't
            // re-fetch).
            TriageVerdict? quick;
            string commentBody;
            try
            {
                (quick, commentBody) = await _triage.QuickTriageAsync(n, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            BranchInfo? branch = null;
            try
            {
                branch = await _resolver.ResolveAsync(n, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

            if (!byBranch.TryGetValue(branch.Key, out var batch))
            {
                batch = new BranchBatch(branch);
                byBranch[branch.Key] = batch;
            }

            batch.Items.Add(new NotificationWithBody(n, commentBody));
            batch.QuickShortcut ??= quick;  // remember any L1 shortcut-actionable for pass 2
        }

        // ============================================================================
        // Pass 2: one agent triage + one dispatch per branch, no matter how many
        // notifications it covers. The agent sees every notification's body inline so
        // it can weigh related events together.
        // ============================================================================
        foreach (var (branchKey, batch) in byBranch)
        {
            var primaryNotificationId = batch.PrimaryNotificationId;
            var commentIds = batch.Items.Select(i => NotificationPoller.DeriveCommentId(i.Notification)).ToList();

            // ---------- L3 agent triage in scratch ----------
            TriageVerdict verdict;
            if (batch.QuickShortcut is { Action: TriageAction.Actionable } shortcut)
            {
                verdict = shortcut;
            }
            else
            {
                try
                {
                    verdict = await _triage.AgentTriageAsync(batch.Items, batch.Branch, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex,
                        "agent triage threw branch={Branch} count={Count}",
                        branchKey, batch.Items.Count);
                    await MarkAllAsync(batch, $"failed:agent-triage:{branchKey}", cancellationToken);
                    continue;
                }
            }

            if (verdict.Action == TriageAction.Drop)
            {
                dropped += batch.Items.Count;
                _logger.LogInformation(
                    "verdict branch={Branch} count={Count} action=Drop why={Why} (L3)",
                    branchKey, batch.Items.Count, verdict.Why);
                await MarkAllAsync(batch, $"dropped:{verdict.Why}", cancellationToken);
                continue;
            }

            actionable++;                       // count once per branch — the unit of dispatch
            coalesced += batch.Items.Count - 1; // any extras on the same branch were absorbed
            _logger.LogInformation(
                "verdict branch={Branch} count={Count} action=Actionable summary={Summary} why={Why} confidence={Confidence:F2}",
                branchKey, batch.Items.Count, verdict.Summary, verdict.Why, verdict.Confidence);

            DispatchOutcome dispatchOutcome;
            try
            {
                dispatchOutcome = await _dispatcher.DispatchAsync(batch.Branch, batch.Items, verdict, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "dispatch threw branch={Branch}", branchKey);
                dispatchOutcome = DispatchOutcome.Failed;
            }

            var outcome = dispatchOutcome switch
            {
                DispatchOutcome.Spawned => $"spawned:{branchKey}",
                DispatchOutcome.HeadsUp => $"heads_up:{branchKey}",
                _ => $"failed:{branchKey}",
            };
            await MarkAllAsync(batch, outcome, cancellationToken);
        }

        if (seen > 0)
            _logger.LogInformation(
                "tick seen={Seen} dropped={Dropped} actionable={Actionable} coalesced={Coalesced} branches={Branches}",
                seen, dropped, actionable, coalesced, byBranch.Count);
        else
            _logger.LogDebug("tick (no new notifications)");
    }

    async Task MarkAllAsync(BranchBatch batch, string outcome, CancellationToken cancellationToken)
    {
        foreach (var item in batch.Items)
        {
            var commentId = NotificationPoller.DeriveCommentId(item.Notification);
            await _stateStore.MarkProcessedAsync(item.Notification.Id, commentId, outcome, cancellationToken);
        }
    }

    /// <summary>
    /// Per-branch buffer accumulated during pass 1 of <see cref="TickAsync"/>. Each branch
    /// produces exactly one agent triage and at most one dispatch in pass 2.
    /// </summary>
    sealed class BranchBatch
    {
        public BranchInfo Branch { get; }
        public List<NotificationWithBody> Items { get; } = new();

        /// <summary>
        /// First non-null actionable verdict produced by L1 (e.g. a future short-circuit).
        /// Pass 2 prefers this over running L3 when present.
        /// </summary>
        public TriageVerdict? QuickShortcut { get; set; }

        public BranchBatch(BranchInfo branch) { Branch = branch; }

        /// <summary>The id of the most recent notification in the batch (by updated_at).</summary>
        public string PrimaryNotificationId => Items
            .OrderByDescending(i => i.Notification.UpdatedAt)
            .First().Notification.Id;
    }
}
