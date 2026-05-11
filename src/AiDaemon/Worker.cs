using System.Diagnostics.Metrics;
using System.Text;
using AiDaemon.Configuration;
using AiDaemon.Models;
using AiDaemon.Services;
using AiDaemon.Storage;
using Microsoft.Extensions.Options;

namespace AiDaemon;

public class Worker : BackgroundService
{
    /// <summary>
    /// Telemetry surface. Listeners are opt-in (no overhead if no MeterListener attached);
    /// the metrics are intended for whatever Prometheus / OpenTelemetry / dotnet-counters
    /// scrape an operator wires up later. The names are stable contract.
    /// </summary>
    static readonly Meter Meter = new("AiDaemon", "1.0.0");
    static readonly Counter<long> TickSeen = Meter.CreateCounter<long>("aidaemon.tick.seen", description: "Notifications observed in pass 1 of a tick.");
    static readonly Counter<long> TickDropped = Meter.CreateCounter<long>("aidaemon.tick.dropped", description: "Notifications dropped at L1/L2/L3.");
    static readonly Counter<long> TickActionable = Meter.CreateCounter<long>("aidaemon.tick.actionable", description: "Branches that produced a real spawn or heads-up.");
    static readonly Counter<long> TickFailed = Meter.CreateCounter<long>("aidaemon.tick.failed", description: "Branches whose dispatch failed plus per-notification quick-triage / resolve failures.");
    static readonly Counter<long> TickCoalesced = Meter.CreateCounter<long>("aidaemon.tick.coalesced", description: "Extra notifications absorbed into a peer notification's branch batch (savings vs. one-dispatch-per-notification).");

    readonly ILogger<Worker> _logger;
    readonly IOptions<DaemonOptions> _options;
    readonly IHostApplicationLifetime _lifetime;
    readonly IStateStore _stateStore;
    readonly INotificationPoller _poller;
    readonly ITriagePipeline _triage;
    readonly IBranchResolver _resolver;
    readonly IDispatcher _dispatcher;
    readonly IGhClient _gh;
    readonly INotificationPusher _pusher;

    /// <summary>
    /// Single-instance lock. We use an exclusive file handle (FileShare.None) instead of a
    /// named Mutex because Mutex is thread-affine: only the thread that called WaitOne can
    /// call ReleaseMutex, and StartAsync / StopAsync may run on different pool threads — the
    /// resulting ApplicationException leaves the mutex abandoned, generating a noisy warning
    /// on every restart. A file handle has no thread affinity, the OS releases it on either
    /// clean exit or crash, and the file doubles as a PID dropbox for diagnostics.
    /// </summary>
    FileStream? _instanceLock;

    public Worker(
        ILogger<Worker> logger,
        IOptions<DaemonOptions> options,
        IHostApplicationLifetime lifetime,
        IStateStore stateStore,
        INotificationPoller poller,
        ITriagePipeline triage,
        IBranchResolver resolver,
        IDispatcher dispatcher,
        IGhClient gh,
        INotificationPusher pusher)
    {
        _logger = logger;
        _options = options;
        _lifetime = lifetime;
        _stateStore = stateStore;
        _poller = poller;
        _triage = triage;
        _resolver = resolver;
        _dispatcher = dispatcher;
        _gh = gh;
        _pusher = pusher;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDir = _options.Value.DataDir;
        Directory.CreateDirectory(dataDir);
        var lockPath = Path.Combine(dataDir, "aidaemon.lock");

        try
        {
            // FileShare.Read so an operator can `Get-Content aidaemon.lock` to see the PID
            // without breaking the exclusion. FileShare.None for write keeps a second daemon out.
            _instanceLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _instanceLock.SetLength(0);
            var pid = Encoding.UTF8.GetBytes($"{Environment.ProcessId}{Environment.NewLine}");
            await _instanceLock.WriteAsync(pid, cancellationToken);
            await _instanceLock.FlushAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            _instanceLock?.Dispose();
            _instanceLock = null;
            _logger.LogCritical(ex,
                "Another instance of AiDaemon is already running (lock file '{Path}' is held). Exiting.",
                lockPath);
            _lifetime.StopApplication();
            return;
        }

        await _stateStore.InitializeAsync(cancellationToken);

        // gh-auth probe. We don't want every poll's "no notifications" log entry to be the
        // first hint that gh isn't logged in — surface the configuration mistake at startup
        // with a single explicit message AND a high-priority ntfy push so an operator who
        // walked away from the laptop sees it on their phone. We intentionally don't
        // fail-fast: the operator might `gh auth login` while we're running, and the actual
        // poll path has its own GhAuthException handling.
        await ProbeGhAuthAsync(cancellationToken);

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
        // Release the lock AFTER the host has signalled stoppingToken and waited for
        // ExecuteAsync to unwind. Releasing earlier would let a fast-restarting second
        // instance acquire the lock during our shutdown grace period — two daemons running
        // concurrently against the same state store and worktrees.
        await base.StopAsync(cancellationToken);

        _instanceLock?.Dispose();
        _instanceLock = null;
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

    /// <summary>
    /// Two-step startup probe: <c>gh auth status</c> (offline — local token shape + scopes)
    /// followed by <c>gh api /user</c> (online — live token validation). Either failing
    /// fires a high-priority ntfy alert so the operator's phone buzzes; the daemon keeps
    /// running because a `gh auth login` mid-flight is a normal recovery path.
    /// </summary>
    internal async Task ProbeGhAuthAsync(CancellationToken cancellationToken)
    {
        string? statusReport = null;
        try
        {
            statusReport = await _gh.AuthStatusAsync(cancellationToken);
            // gh's report is multi-line; log it at Information so an operator grepping the
            // log can see exactly which host / scopes are in play without re-running gh.
            _logger.LogInformation("gh auth status:\n{Report}", statusReport);
        }
        catch (GhAuthException ex)
        {
            _logger.LogCritical(ex,
                "gh auth status reports no authentication. Run `gh auth login` to authenticate.");
            await TryPushAuthAlertAsync(
                "AiDaemon: gh not authenticated",
                $"`gh auth status` failed at startup. Run `gh auth login`.\n\n```\n{ex.Stderr.Trim()}\n```",
                cancellationToken);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "gh auth status probe failed at startup — continuing degraded");
            // Don't push for unknown failures (e.g. gh CLI missing entirely): the live
            // /user probe below will catch the same thing with a better diagnostic.
        }

        try
        {
            var login = await _gh.WhoAmIAsync(cancellationToken);
            _logger.LogInformation("gh authenticated as {Login}",
                string.IsNullOrEmpty(login) ? "(no login field returned)" : login);

            if (!string.IsNullOrEmpty(_options.Value.AiUserLogin)
                && !string.Equals(login, _options.Value.AiUserLogin, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "gh login {Actual} does not match configured AiUserLogin {Configured} — self-authored notifications will not be filtered",
                    login, _options.Value.AiUserLogin);
            }
        }
        catch (GhAuthException ex)
        {
            _logger.LogCritical(ex,
                "gh /user returned an auth failure. The daemon will tick but every poll will fail until `gh auth login` is run.");
            await TryPushAuthAlertAsync(
                "AiDaemon: gh token invalid",
                $"`gh api /user` failed at startup. Run `gh auth login`.\n\n```\n{ex.Stderr.Trim()}\n```",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "gh /user probe failed at startup — continuing degraded");
            await TryPushAuthAlertAsync(
                "AiDaemon: gh probe failed",
                $"`gh api /user` threw at startup: {ex.GetType().Name}: {ex.Message}",
                cancellationToken);
        }
    }

    /// <summary>
    /// Push wrapper that swallows its own failures. An ntfy outage during startup must not
    /// prevent the daemon from coming up — the log already carries the diagnostic.
    /// </summary>
    async Task TryPushAuthAlertAsync(string title, string body, CancellationToken cancellationToken)
    {
        try
        {
            await _pusher.PushAlertAsync(title, body, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "auth-alert push failed (continuing)");
        }
    }

    /// <summary>Retention horizon for the dedup table. Anything older than this is safe to drop:
    /// the cursor has long since advanced past it and no notification re-fires from history.</summary>
    static readonly TimeSpan ProcessedRetention = TimeSpan.FromDays(30);

    /// <summary>Cadence for the daily-style prune call. Runs at most once per period regardless of tick frequency.</summary>
    static readonly TimeSpan ProcessedPruneInterval = TimeSpan.FromHours(24);

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await TryPruneProcessedAsync(cancellationToken);

        var seen = 0;
        var dropped = 0;
        var actionable = 0;
        var coalesced = 0;
        var failed = 0;

        // ============================================================================
        // Pass 1: poll every notification, run cheap L1+L2 filters, resolve to a branch,
        // group survivors by branch.Key. Notifications that drop or fail to resolve are
        // marked here and never reach pass 2.
        // ============================================================================
        var byBranch = new Dictionary<string, BranchBatch>(StringComparer.Ordinal);

        // Per-tick resolve cache. Multiple notifications on the same PR / issue would
        // otherwise each fire a `gh api /pulls/N` and a `git rev-parse`; keying on
        // (repo|subject.url) lets us pay that cost once per tick. Lifetime is the tick.
        var resolveCache = new Dictionary<string, BranchInfo?>(StringComparer.Ordinal);

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
            string commentAuthor;
            try
            {
                (quick, commentBody, commentAuthor) = await _triage.QuickTriageAsync(n, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The poller has already advanced its in-memory cursor past this notification.
                // If we just `continue` without recording an outcome, the row is silently lost
                // forever. Mark it processed with a failure-flavored outcome so the dedupe
                // table reflects what happened and an operator can see the trail in SQLite.
                _logger.LogError(ex, "quick triage threw thread={ThreadId} — marking failed", n.Id);
                failed++;
                await _stateStore.MarkProcessedAsync(n.Id, commentId, $"failed:quick-triage:{ex.GetType().Name}", cancellationToken);
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
            string? resolveOutcome = null;
            var cacheKey = $"{n.Repository.FullName}|{n.Subject.Url}";
            if (resolveCache.TryGetValue(cacheKey, out var cached))
            {
                branch = cached;
            }
            else
            {
                try
                {
                    branch = await _resolver.ResolveAsync(n, cancellationToken);
                    resolveCache[cacheKey] = branch;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "branch resolve threw thread={ThreadId}", n.Id);
                    failed++;
                    resolveOutcome = $"failed:resolve:{ex.GetType().Name}";
                    // Don't cache exception results — a transient failure shouldn't poison
                    // the rest of the tick for sibling notifications on the same subject.
                }
            }

            if (branch == null)
            {
                await _stateStore.MarkProcessedAsync(n.Id, commentId, resolveOutcome ?? "unresolved", cancellationToken);
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

            batch.Items.Add(new NotificationWithBody(n, commentBody, commentAuthor));
        }

        // ============================================================================
        // Pass 2: one agent triage + one dispatch per branch, no matter how many
        // notifications it covers. The agent sees every notification's body inline so
        // it can weigh related events together.
        // ============================================================================
        foreach (var (branchKey, batch) in byBranch)
        {
            // ---------- L3 agent triage in scratch ----------
            TriageVerdict verdict;
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

            if (verdict.Action == TriageAction.Drop)
            {
                dropped += batch.Items.Count;
                _logger.LogInformation(
                    "verdict branch={Branch} count={Count} action=Drop why={Why} (L3)",
                    branchKey, batch.Items.Count, verdict.Why);
                await MarkAllAsync(batch, $"dropped:{verdict.Why}", cancellationToken);
                continue;
            }

            // Any extras on the same branch were absorbed into this batch regardless of
            // whether dispatch ultimately succeeds; the work of grouping them happened.
            coalesced += batch.Items.Count - 1;
            _logger.LogInformation(
                "verdict branch={Branch} count={Count} action=Actionable why={Why} confidence={Confidence:F2}",
                branchKey, batch.Items.Count, verdict.Why, verdict.Confidence);

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

            // 'actionable' is the headline figure for the human reading the tick log: the
            // number of branches that produced a real spawn or heads-up. Failures get
            // their own bucket so a flaky run doesn't masquerade as a productive one.
            if (dispatchOutcome == DispatchOutcome.Failed)
                failed++;
            else
                actionable++;

            // Charge the per-thread rate limit only when the daemon actually took action.
            // Dropped or failed dispatches don't consume budget, so noisy LGTM threads can't
            // squeeze out the substantive comment that lands later in the day.
            if (dispatchOutcome != DispatchOutcome.Failed)
            {
                var today = DateOnly.FromDateTime(DateTime.UtcNow);
                foreach (var threadId in batch.Items.Select(i => i.Notification.Id).Distinct(StringComparer.Ordinal))
                    await _stateStore.IncrementRateLimitAsync(threadId, today, cancellationToken);
            }

            var outcome = dispatchOutcome switch
            {
                DispatchOutcome.Spawned => $"spawned:{branchKey}",
                DispatchOutcome.HeadsUp => $"heads_up:{branchKey}",
                _ => $"failed:{branchKey}",
            };
            await MarkAllAsync(batch, outcome, cancellationToken);
        }

        TickSeen.Add(seen);
        TickDropped.Add(dropped);
        TickActionable.Add(actionable);
        TickFailed.Add(failed);
        TickCoalesced.Add(coalesced);

        if (seen > 0)
            _logger.LogInformation(
                "tick seen={Seen} dropped={Dropped} actionable={Actionable} failed={Failed} coalesced={Coalesced} branches={Branches}",
                seen, dropped, actionable, failed, coalesced, byBranch.Count);
        else
            _logger.LogDebug("tick (no new notifications)");
    }

    /// <summary>
    /// Gated daily prune. The processed table accumulates one row per (thread_id, comment_id)
    /// dedup pair; over months that's a few thousand rows on a busy account. The kv key
    /// holds the last successful prune's UTC time, so this is O(kv-read) on every tick that
    /// doesn't fire.
    /// </summary>
    async Task TryPruneProcessedAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await _stateStore.GetKvAsync(StateStoreKeys.ProcessedLastPruned, cancellationToken);
            if (DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var lastPruned)
                && DateTimeOffset.UtcNow - lastPruned < ProcessedPruneInterval)
            {
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - ProcessedRetention;
            var deleted = await _stateStore.PruneProcessedAsync(cutoff, cancellationToken);
            await _stateStore.SetKvAsync(
                StateStoreKeys.ProcessedLastPruned,
                DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);

            if (deleted > 0)
                _logger.LogInformation(
                    "pruned {Deleted} processed rows older than {Cutoff:O}", deleted, cutoff);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Prune failures must never block the actual tick work.
            _logger.LogWarning(ex, "processed-table prune failed (will retry on the next tick after the gate clears)");
        }
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

        public BranchBatch(BranchInfo branch) { Branch = branch; }
    }
}
