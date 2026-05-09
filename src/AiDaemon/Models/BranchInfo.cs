namespace AiDaemon.Models;

/// <summary>
/// The result of resolving a notification to a local worktree.
/// </summary>
/// <param name="Repo">"owner/repo" — must be in <c>RepoAllowlist</c>.</param>
/// <param name="Branch">The git branch the worktree is checked out on (also the head.ref for PRs).</param>
/// <param name="Worktree">Absolute path to the worktree directory.</param>
/// <param name="PrNumber">Set when the notification's subject is a PullRequest.</param>
/// <param name="IssueNumber">Set when the notification's subject is an Issue.</param>
public record BranchInfo(
    string Repo,
    string Branch,
    string Worktree,
    int? PrNumber,
    int? IssueNumber)
{
    /// <summary>"owner/repo:branch" — the canonical key in the branches state table.</summary>
    public string Key => $"{Repo}:{Branch}";
}
