using AiDaemon.Models;

namespace AiDaemon.Services;

public interface IRcLauncher
{
    /// <summary>
    /// Opens a visible cmd.exe window in <paramref name="branch"/>'s worktree running
    /// <c>claude --remote-control &lt;name&gt;</c> (fresh) or
    /// <c>claude --resume &lt;sessionId&gt; --remote-control &lt;name&gt;</c> (continuation),
    /// waits for the relay to register the bridge, and returns the captured PIDs + URL.
    /// Writes a <c>.daemon-active</c> marker into the worktree.
    /// </summary>
    /// <param name="sessionId">Pass <c>null</c> on first spawn; on respawn pass the previously captured value.</param>
    Task<RcAttachment> SpawnRcAsync(BranchInfo branch, string? sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Kills the cmd.exe process tree (which takes the child <c>claude.exe</c> with it),
    /// removes the <c>.daemon-active</c> marker. The conversation JSONL is left intact.
    /// Best-effort: never throws.
    /// </summary>
    Task CleanupAsync(BranchState rec, CancellationToken cancellationToken);

    /// <summary>
    /// True iff (a) the claude PID is still running with the same StartTime ticks (PID-recycle
    /// guard) AND (b) the per-PID registry file still carries a non-null bridgeSessionId.
    /// Anything short of both is "not alive" — the caller should clean up and respawn.
    /// Async because the bridge-id check retries once after 100ms to ride out a registry
    /// rewrite landing mid-read.
    /// </summary>
    Task<bool> IsAliveAsync(int claudePid, long claudeStartTicks, CancellationToken cancellationToken);
}
