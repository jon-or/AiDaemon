using Microsoft.Extensions.Options;

namespace AiDaemon.Configuration;

public class DaemonOptions
{
    public const string SectionName = "Daemon";

    public int PollIntervalSeconds { get; set; } = 60;
    public string AiUserLogin { get; set; } = "";
    public string WorktreeRoot { get; set; } = "";
    public string ClaudePath { get; set; } = "claude";
    public string GhPath { get; set; } = "gh";
    public List<string> RepoAllowlist { get; set; } = new();

    /// <summary>
    /// Per-repo path to the main clone, keyed by <c>"owner/repo"</c>. When a notification
    /// arrives for a branch with no existing worktree under <see cref="WorktreeRoot"/>, the
    /// resolver shells <c>git -C &lt;RepoRoots[repo]&gt; worktree add &lt;WorktreeRoot&gt;\&lt;branch&gt;
    /// &lt;branch&gt;</c> to materialize one. The branch must already exist locally — if it
    /// doesn't (cross-fork PR, unfetched ref), the resolver silently skips the notification.
    /// A repo with no entry here keeps the legacy behavior: skip notifications whose worktree
    /// isn't already on disk.
    /// </summary>
    public Dictionary<string, string> RepoRoots { get; set; } = new();
    public List<string> ActionableReasons { get; set; } = new();
    public List<string> BotAuthorBlocklist { get; set; } = new();
    public int RcIdleTimeoutHours { get; set; } = 2;
    public string DataDir { get; set; } = @"C:\ProgramData\AiDaemon";

    public TriageOptions Triage { get; set; } = new();
    public NtfyOptions Ntfy { get; set; } = new();
}

/// <summary>
/// Fail startup with a clear, actionable message when DaemonOptions is missing the things
/// the daemon needs to do anything useful. Without this, a misconfigured deploy hits its
/// first call site at runtime and produces an opaque error from somewhere deep in the
/// pipeline (e.g. BranchResolver logging "WorktreeRoot is not configured" every poll).
/// </summary>
public class DaemonOptionsValidator : IValidateOptions<DaemonOptions>
{
    public ValidateOptionsResult Validate(string? name, DaemonOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.WorktreeRoot))
            failures.Add($"{nameof(options.WorktreeRoot)} is required (e.g. \"D:\\\\git\\\\orez.worktrees\")");

        if (string.IsNullOrWhiteSpace(options.AiUserLogin))
            failures.Add($"{nameof(options.AiUserLogin)} is required — used to drop self-authored notifications at L1");

        if (options.RepoAllowlist.Count == 0)
            failures.Add($"{nameof(options.RepoAllowlist)} must contain at least one \"owner/repo\" entry");

        if (options.ActionableReasons.Count == 0)
            failures.Add($"{nameof(options.ActionableReasons)} must list the GitHub notification reasons that trigger triage (e.g. mention, review_requested)");

        if (options.PollIntervalSeconds < 1)
            failures.Add($"{nameof(options.PollIntervalSeconds)} must be >= 1 (got {options.PollIntervalSeconds})");

        if (options.RcIdleTimeoutHours < 1)
            failures.Add($"{nameof(options.RcIdleTimeoutHours)} must be >= 1 (got {options.RcIdleTimeoutHours})");

        if (options.Triage.MaxActionsPerThreadPerDay < 1)
            failures.Add($"{nameof(options.Triage)}.{nameof(options.Triage.MaxActionsPerThreadPerDay)} must be >= 1 (got {options.Triage.MaxActionsPerThreadPerDay})");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public class TriageOptions
{
    /// <summary>Model used by the L3 classifier in scratch dir (just decides actionable/drop).</summary>
    public string Model { get; set; } = "haiku";

    /// <summary>Model used by the headless pre-run agent in the worktree (does the actual research/fix).</summary>
    public string PreRunModel { get; set; } = "opus";

    public int MaxActionsPerThreadPerDay { get; set; } = 5;
    public List<string> L2DropPatterns { get; set; } = new();

    /// <summary>
    /// When true (default), PR notifications fired by a pure approval (review state=APPROVED
    /// with empty body) drop at L1.5 instead of being sent to the LLM. The classifier was
    /// already getting these right ~all the time, but each one costs an LLM call for no
    /// value — the daemon's not going to "respond" to a thumbs-up. Set to false to route
    /// approvals through the agent anyway (debugging, audit-mode).
    /// </summary>
    public bool DropEmptyApprovals { get; set; } = true;
}

public class NtfyOptions
{
    public string Server { get; set; } = "https://ntfy.sh";
    public string Topic { get; set; } = "";
    public int PriorityNormal { get; set; } = 3;
    public int PriorityHigh { get; set; } = 4;
}
