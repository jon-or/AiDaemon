using AiDaemon.Models;

namespace AiDaemon.Services;

public interface IBranchResolver
{
    /// <summary>
    /// Resolves the given notification to a local worktree, or returns <c>null</c> if the
    /// notification should be skipped (out-of-scope repo, no matching worktree, or branch
    /// mismatch). All skip reasons are logged.
    /// </summary>
    Task<BranchInfo?> ResolveAsync(GhNotification notification, CancellationToken cancellationToken);
}
