namespace AiDaemon.Models;

/// <summary>
/// The PIDs and IDs captured after a successful <see cref="Services.IRcLauncher.SpawnRcAsync"/>.
/// Persisted into the branches table so subsequent ticks can liveness-check the session.
/// </summary>
/// <param name="PsPid">PowerShell launcher PID. Used by CleanupAsync to <c>Kill(entireProcessTree)</c>.</param>
/// <param name="ClaudePid">The inner <c>claude.exe</c> PID. The "session owner" — owns the registry file and the bridge.</param>
/// <param name="ClaudeStartTicks"><c>Process.StartTime.Ticks</c> at attach. Defends against Windows PID recycling.</param>
/// <param name="BridgeSessionId">The relay's per-attachment ID, e.g. <c>session_01CB6duHUCCEQzuT336LxqJ8</c>.</param>
/// <param name="Url">The user-facing <c>https://claude.ai/code/&lt;bridgeSessionId&gt;</c> URL.</param>
/// <param name="SessionId">The durable Claude UUID. On a fresh spawn this comes from the registry file; on a resume it equals the value passed to <c>SpawnRcAsync</c>.</param>
public record RcAttachment(
    int PsPid,
    int ClaudePid,
    long ClaudeStartTicks,
    string BridgeSessionId,
    string Url,
    string SessionId);

public enum DispatchOutcome
{
    /// <summary>A new RC session was spawned and a session-link push was emitted.</summary>
    Spawned,

    /// <summary>RC was already alive and a heads-up push was emitted against the existing URL.</summary>
    HeadsUp,

    /// <summary>The dispatch attempt failed before reaching the spawn path; no push was emitted.</summary>
    Failed,
}
