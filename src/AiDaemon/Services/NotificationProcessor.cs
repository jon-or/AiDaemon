using AiDaemon.Models;
using AiDaemon.Storage;
using Microsoft.Extensions.Logging;

namespace AiDaemon.Services;

/// <summary>
/// Single-notification reincarnation of <see cref="Worker.TickAsync"/>. The poll path keeps
/// its per-tick streaming + branch-batch coalescing; this service is the entry point the
/// tray Retry uses. We deliberately do NOT consume the rate-limit budget here: the operator
/// clicking Retry is making a manual override and shouldn't be punished by the actionable
/// allocation that exists to throttle automated spawn storms.
///
/// The outcome vocabulary written to the <c>processed</c> table mirrors the poll path
/// (<c>dropped:</c>, <c>unresolved</c>, <c>spawned:</c>, <c>heads-up:</c>, <c>failed:*</c>)
/// so an operator inspecting state.db sees one consistent set of strings regardless of how
/// the row was last touched.
/// </summary>
public class NotificationProcessor : INotificationProcessor
{
    readonly IStateStore _stateStore;
    readonly ITriagePipeline _triage;
    readonly IBranchResolver _resolver;
    readonly IDispatcher _dispatcher;
    readonly ILogger<NotificationProcessor> _logger;

    public NotificationProcessor(
        IStateStore stateStore,
        ITriagePipeline triage,
        IBranchResolver resolver,
        IDispatcher dispatcher,
        ILogger<NotificationProcessor> logger)
    {
        _stateStore = stateStore;
        _triage = triage;
        _resolver = resolver;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<RetryOutcome> ProcessOneAsync(GhNotification n, CancellationToken cancellationToken)
    {
        var commentId = NotificationPoller.DeriveCommentId(n);
        var context = ProcessedContext.From(n);

        _logger.LogInformation(
            "retry processing thread={ThreadId} repo={Repo} type={Type} title={Title}",
            n.Id, n.Repository.FullName, n.Subject.Type, n.Subject.Title);

        // ---- L1 + L2 quick triage ----
        TriageVerdict? quick;
        string commentBody;
        string commentAuthor;
        try
        {
            (quick, commentBody, commentAuthor) = await _triage.QuickTriageAsync(n, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "retry quick-triage threw thread={ThreadId}", n.Id);
            await _stateStore.MarkProcessedAsync(
                n.Id, commentId, $"failed:quick-triage:{ex.GetType().Name}", context, cancellationToken);
            return RetryOutcome.Failed;
        }

        if (quick is { Action: TriageAction.Drop })
        {
            _logger.LogInformation(
                "retry verdict thread={ThreadId} action=Drop why={Why} (L1/L2)", n.Id, quick.Why);
            await _stateStore.MarkProcessedAsync(
                n.Id, commentId, $"dropped:{quick.Why}", context, cancellationToken);
            return RetryOutcome.Dropped;
        }

        // ---- Branch resolve ----
        BranchInfo? branch;
        try
        {
            branch = await _resolver.ResolveAsync(n, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "retry resolve threw thread={ThreadId}", n.Id);
            await _stateStore.MarkProcessedAsync(
                n.Id, commentId, $"failed:resolve:{ex.GetType().Name}", context, cancellationToken);
            return RetryOutcome.Failed;
        }

        if (branch == null)
        {
            _logger.LogInformation("retry unresolved thread={ThreadId}", n.Id);
            await _stateStore.MarkProcessedAsync(n.Id, commentId, "unresolved", context, cancellationToken);
            return RetryOutcome.Unresolved;
        }

        // ---- Prior-comment enrichment + L3 agent triage on a one-item batch ----
        // Enrichment is best-effort: the pipeline swallows gh failures and any unexpected
        // escape here falls back to the un-enriched item. Triage must not be brittle to
        // the comment-list endpoint being unavailable.
        IReadOnlyList<NotificationWithBody> items = [new NotificationWithBody(n, commentBody, commentAuthor)];
        try
        {
            items = await _triage.EnrichWithPriorCommentsAsync(items, branch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "retry prior-comment enrichment threw branch={Branch} — proceeding without", branch.Key);
        }

        TriageVerdict verdict;
        try
        {
            verdict = await _triage.AgentTriageAsync(items, branch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "retry agent-triage threw branch={Branch}", branch.Key);
            await _stateStore.MarkProcessedAsync(
                n.Id, commentId, $"failed:agent-triage:{branch.Key}", context, cancellationToken);
            return RetryOutcome.Failed;
        }

        if (verdict.Action == TriageAction.Drop)
        {
            _logger.LogInformation(
                "retry verdict thread={ThreadId} branch={Branch} action=Drop why={Why} (L3)",
                n.Id, branch.Key, verdict.Why);
            await _stateStore.MarkProcessedAsync(
                n.Id, commentId, $"dropped:agent:{verdict.Why}", context, cancellationToken);
            return RetryOutcome.Dropped;
        }

        // ---- Dispatch ----
        DispatchOutcome dispatched;
        try
        {
            dispatched = await _dispatcher.DispatchAsync(branch, items, verdict, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "retry dispatch threw branch={Branch}", branch.Key);
            await _stateStore.MarkProcessedAsync(
                n.Id, commentId, $"failed:dispatch:{ex.GetType().Name}", context, cancellationToken);
            return RetryOutcome.Failed;
        }

        var outcomeString = dispatched switch
        {
            DispatchOutcome.Spawned => $"spawned:{branch.Key}",
            DispatchOutcome.HeadsUp => $"heads-up:{branch.Key}",
            DispatchOutcome.Failed  => $"failed:dispatch:{branch.Key}",
            _                       => $"unknown:{dispatched}",
        };
        await _stateStore.MarkProcessedAsync(n.Id, commentId, outcomeString, context, cancellationToken);

        return dispatched switch
        {
            DispatchOutcome.Spawned => RetryOutcome.Spawned,
            DispatchOutcome.HeadsUp => RetryOutcome.HeadsUp,
            DispatchOutcome.Failed  => RetryOutcome.Failed,
            _                       => RetryOutcome.Failed,
        };
    }
}
