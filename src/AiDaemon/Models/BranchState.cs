namespace AiDaemon.Models;

public class BranchState
{
    /// <summary>"owner/repo:412-fix-x"</summary>
    public string Branch { get; set; } = "";

    /// <summary>Durable Claude session UUID. Persisted across RC respawns.</summary>
    public string SessionId { get; set; } = "";

    public string Worktree { get; set; } = "";
    public BranchMode Mode { get; set; } = BranchMode.Idle;

    public int? RcPid { get; set; }
    public int? RcClaudePid { get; set; }

    /// <summary>Process.StartTime ticks; defends against PID recycling.</summary>
    public long? RcClaudeStart { get; set; }

    public string? RcBridgeId { get; set; }
    public string? RcUrl { get; set; }

    /// <summary>Unix seconds.</summary>
    public long LastEventAt { get; set; }

    public int? PrNumber { get; set; }
    public int? IssueNumber { get; set; }
}
