using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Models;
using AiDaemon.Process;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

public class BranchResolver : IBranchResolver
{
    /// <summary>
    /// Per-call cap on <c>git rev-parse</c>. The worktree's filesystem could hang
    /// (network drive, antivirus lock, msysgit hiccup); a stuck git would otherwise
    /// freeze the entire poll loop indefinitely.
    /// </summary>
    static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);

    readonly IGhClient _gh;
    readonly IFileSystem _fs;
    readonly IProcessRunner _runner;
    readonly DaemonOptions _options;
    readonly ILogger<BranchResolver> _logger;

    readonly HashSet<string> _allowlist;

    public BranchResolver(
        IGhClient gh,
        IFileSystem fs,
        IProcessRunner runner,
        IOptions<DaemonOptions> options,
        ILogger<BranchResolver> logger)
    {
        _gh = gh;
        _fs = fs;
        _runner = runner;
        _options = options.Value;
        _logger = logger;

        _allowlist = new HashSet<string>(_options.RepoAllowlist, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<BranchInfo?> ResolveAsync(GhNotification n, CancellationToken cancellationToken)
    {
        if (!_allowlist.Contains(n.Repository.FullName))
        {
            _logger.LogWarning(
                "out-of-scope repo {Repo} (not in RepoAllowlist) thread={ThreadId}",
                n.Repository.FullName, n.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(_options.WorktreeRoot))
        {
            _logger.LogError("WorktreeRoot is not configured — cannot resolve any notification");
            return null;
        }

        var subjectNumber = ParseLastSegmentInt(n.Subject.Url);
        if (subjectNumber == null)
        {
            _logger.LogWarning(
                "could not parse issue/PR number from subject.url {Url} thread={ThreadId}",
                n.Subject.Url, n.Id);
            return null;
        }

        return n.Subject.Type switch
        {
            "PullRequest" => await ResolvePrAsync(n, subjectNumber.Value, cancellationToken),
            "Issue" => await ResolveIssueAsync(n, subjectNumber.Value, cancellationToken),
            _ => LogUnsupportedAndReturnNull(n),
        };
    }

    async Task<BranchInfo?> ResolvePrAsync(GhNotification n, int prNumber, CancellationToken cancellationToken)
    {
        PrInfo pr;
        try
        {
            pr = await _gh.GetPullRequestAsync(n.Repository.FullName, prNumber, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "failed to fetch PR {Repo}#{Pr} thread={ThreadId}",
                n.Repository.FullName, prNumber, n.Id);
            return null;
        }

        var branch = pr.Head.Ref;
        if (string.IsNullOrWhiteSpace(branch))
        {
            _logger.LogWarning("PR {Repo}#{Pr} returned empty head.ref thread={ThreadId}",
                n.Repository.FullName, prNumber, n.Id);
            return null;
        }

        // Worktree directory naming convention: <issue>-<slug>, which usually equals head.ref.
        var worktree = Path.Combine(_options.WorktreeRoot, branch);
        if (!_fs.DirectoryExists(worktree))
        {
            // Fallback: try globbing by the leading numeric prefix of head.ref. Branches like
            // "16119-isdpvirtualproperty" still resolve even if the worktree was created with a
            // slightly different slug.
            var prefix = LeadingNumericPrefix(branch);
            worktree = (prefix != null ? FindWorktreeByPrefix(prefix) : null)
                ?? worktree;

            if (!_fs.DirectoryExists(worktree))
            {
                // Nothing on disk — attempt to materialize the worktree from a local ref.
                // Silently skips if no RepoRoots entry, or if the branch isn't a local ref
                // (cross-fork PRs, unfetched refs). That's by design — the human can fetch
                // + retry on the next poll.
                var created = await TryCreateWorktreeAsync(
                    n.Repository.FullName, branch, cancellationToken);
                if (created == null)
                {
                    _logger.LogInformation(
                        "no worktree for PR {Repo}#{Pr} branch={Branch} and auto-create skipped (looked under {WorktreeRoot})",
                        n.Repository.FullName, prNumber, branch, _options.WorktreeRoot);
                    return null;
                }
                worktree = created;
            }
        }

        if (!await ConfirmWorktreeOnBranchAsync(worktree, branch, n.Id, cancellationToken))
            return null;

        // Cross-link to the linked issue: branch convention is "<issue>-<slug>", so the
        // numeric prefix of head.ref is the issue number in the common case. No network
        // call — purely string parsing.
        int? linkedIssue = null;
        if (LeadingNumericPrefix(branch) is string p && int.TryParse(p, out var issueFromBranch))
            linkedIssue = issueFromBranch;

        return new BranchInfo(n.Repository.FullName, branch, worktree, PrNumber: prNumber, IssueNumber: linkedIssue);
    }

    async Task<BranchInfo?> ResolveIssueAsync(GhNotification n, int issueNumber, CancellationToken cancellationToken)
    {
        var worktree = FindWorktreeByPrefix(issueNumber.ToString());
        if (worktree == null)
        {
            // No worktree on disk — try to materialize one from a matching local branch.
            // Convention: branches are "<issue>-<slug>", so a single ref matching
            // refs/heads/<issue>-* is unambiguous. Zero or multiple matches → silent skip.
            worktree = await TryCreateWorktreeForIssueAsync(n, issueNumber, cancellationToken);
            if (worktree == null)
            {
                _logger.LogInformation(
                    "no worktree for issue {Repo}#{Issue} and auto-create skipped (looked under {WorktreeRoot}\\{Issue}-*)",
                    n.Repository.FullName, issueNumber, _options.WorktreeRoot, issueNumber);
                return null;
            }
        }

        var branch = await ReadCurrentBranchAsync(worktree, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
        {
            _logger.LogWarning("git rev-parse failed in {Worktree} thread={ThreadId}", worktree, n.Id);
            return null;
        }

        // Cross-link to the open PR for this branch (if there's exactly one). Costs one
        // gh api call but lets the push surface both Open Issue + Open PR buttons in the
        // common one-issue/one-PR case. Failure to find a PR is fine — we drop the button.
        int? linkedPr = null;
        try
        {
            linkedPr = await _gh.FindOpenPrNumberForBranchAsync(n.Repository.FullName, branch, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "PR cross-link lookup failed for branch={Branch} thread={ThreadId} — proceeding without Open PR button",
                branch, n.Id);
        }

        return new BranchInfo(n.Repository.FullName, branch, worktree, PrNumber: linkedPr, IssueNumber: issueNumber);
    }

    /// <summary>
    /// Confirms the given worktree's current HEAD ref equals <paramref name="expectedBranch"/>.
    /// Logs a warning and returns false on mismatch — we don't try to repair filesystem state
    /// from the daemon.
    /// </summary>
    async Task<bool> ConfirmWorktreeOnBranchAsync(
        string worktree, string expectedBranch, string threadId, CancellationToken cancellationToken)
    {
        var actual = await ReadCurrentBranchAsync(worktree, cancellationToken);
        if (string.IsNullOrWhiteSpace(actual))
        {
            _logger.LogWarning(
                "could not read current branch of worktree {Worktree} thread={ThreadId}",
                worktree, threadId);
            return false;
        }

        if (!string.Equals(actual, expectedBranch, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "worktree branch mismatch: {Worktree} is on {Actual} but PR head is {Expected} thread={ThreadId}",
                worktree, actual, expectedBranch, threadId);
            return false;
        }

        return true;
    }

    async Task<string?> ReadCurrentBranchAsync(string worktree, CancellationToken cancellationToken)
    {
        // `git symbolic-ref --short HEAD` is the precise question we want to ask: "what
        // branch is HEAD pointing at?" In a detached state (mid-rebase, mid-bisect, mid-
        // checkout-commit) it exits 128 with stderr "fatal: ref HEAD is not a symbolic
        // ref" and we report null — exactly the "transitional, skip" semantic the
        // worker needs. The older `git rev-parse --abbrev-ref HEAD` returns the literal
        // string "HEAD" with exit 0 in that case, which then trips the "branch mismatch"
        // warning even though the worktree just happens to be in flight.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(GitTimeout);

        try
        {
            var result = await _runner.RunAsync(
                "git",
                new[] { "-C", worktree, "symbolic-ref", "--short", "HEAD" },
                cancellationToken: cts.Token);

            return result.Succeeded ? result.Stdout.Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "git symbolic-ref in {Worktree} did not return within {Timeout}s — treating as unresolved",
                worktree, GitTimeout.TotalSeconds);
            return null;
        }
    }

    /// <summary>
    /// Shells <c>git -C &lt;repoRoot&gt; worktree add &lt;WorktreeRoot&gt;\&lt;branch&gt; &lt;branch&gt;</c>.
    /// Returns the new worktree path on success, or null when:
    /// <list type="bullet">
    ///   <item><see cref="DaemonOptions.RepoRoots"/> has no entry for the repo.</item>
    ///   <item>The configured repo root doesn't exist on disk.</item>
    ///   <item>git exits non-zero (most commonly: the branch is not a local ref).</item>
    /// </list>
    /// Failure is logged at Information — silent enough to be a normal "branch not local yet"
    /// skip on every poll, loud enough to spot a misconfigured RepoRoots entry in the log.
    /// </summary>
    async Task<string?> TryCreateWorktreeAsync(string repo, string branch, CancellationToken cancellationToken)
    {
        if (!_options.RepoRoots.TryGetValue(repo, out var repoRoot) || string.IsNullOrWhiteSpace(repoRoot))
        {
            _logger.LogDebug(
                "no RepoRoots entry for {Repo} — skipping worktree auto-create for branch {Branch}",
                repo, branch);
            return null;
        }

        if (!_fs.DirectoryExists(repoRoot))
        {
            _logger.LogWarning(
                "RepoRoots[{Repo}] points at {RepoRoot} which does not exist — skipping auto-create for {Branch}",
                repo, repoRoot, branch);
            return null;
        }

        var worktree = Path.Combine(_options.WorktreeRoot, branch);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(GitTimeout);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                "git",
                new[] { "-C", repoRoot, "worktree", "add", worktree, branch },
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "git worktree add for {Repo}:{Branch} did not return within {Timeout}s — skipping",
                repo, branch, GitTimeout.TotalSeconds);
            return null;
        }

        if (!result.Succeeded)
        {
            // Most common reason: branch isn't a local ref (fork PR, or user hasn't fetched
            // recently). Per design, silently skip — they can fetch + retry on next poll.
            _logger.LogInformation(
                "git worktree add failed for {Repo}:{Branch} (exit={Exit}): {Stderr}",
                repo, branch, result.ExitCode, result.Stderr.TrimEnd());
            return null;
        }

        _logger.LogInformation(
            "auto-created worktree {Worktree} for {Repo}:{Branch}", worktree, repo, branch);
        return worktree;
    }

    /// <summary>
    /// Issue notifications don't carry a branch — we infer one from the local ref namespace.
    /// Lists <c>refs/heads/&lt;issue&gt;-*</c> in the configured repo root; only an unambiguous
    /// single match feeds into <see cref="TryCreateWorktreeAsync"/>. Zero matches → branch
    /// hasn't been created yet (the user's workflow hasn't reached that issue). Multiple
    /// matches → ambiguous; we don't pick.
    /// </summary>
    async Task<string?> TryCreateWorktreeForIssueAsync(GhNotification n, int issueNumber, CancellationToken cancellationToken)
    {
        if (!_options.RepoRoots.TryGetValue(n.Repository.FullName, out var repoRoot) || string.IsNullOrWhiteSpace(repoRoot))
        {
            _logger.LogDebug(
                "no RepoRoots entry for {Repo} — skipping issue auto-create for #{Issue}",
                n.Repository.FullName, issueNumber);
            return null;
        }

        if (!_fs.DirectoryExists(repoRoot))
        {
            _logger.LogWarning(
                "RepoRoots[{Repo}] points at {RepoRoot} which does not exist — skipping auto-create for issue #{Issue}",
                n.Repository.FullName, repoRoot, issueNumber);
            return null;
        }

        var branch = await FindLocalBranchForIssueAsync(repoRoot, issueNumber, cancellationToken);
        if (branch == null)
            return null;

        return await TryCreateWorktreeAsync(n.Repository.FullName, branch, cancellationToken);
    }

    async Task<string?> FindLocalBranchForIssueAsync(string repoRoot, int issueNumber, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(GitTimeout);

        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(
                "git",
                new[]
                {
                    "-C", repoRoot, "for-each-ref",
                    "--format=%(refname:short)",
                    $"refs/heads/{issueNumber}-*",
                },
                cancellationToken: cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "git for-each-ref for issue {Issue} did not return within {Timeout}s",
                issueNumber, GitTimeout.TotalSeconds);
            return null;
        }

        if (!result.Succeeded)
        {
            _logger.LogWarning(
                "git for-each-ref refs/heads/{Issue}-* failed (exit={Exit}): {Stderr}",
                issueNumber, result.ExitCode, result.Stderr.TrimEnd());
            return null;
        }

        var matches = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (matches.Count == 0)
            return null;

        if (matches.Count > 1)
        {
            _logger.LogInformation(
                "issue #{Issue} matches multiple local branches ({Branches}) — skipping auto-create",
                issueNumber, string.Join(", ", matches));
            return null;
        }

        return matches[0];
    }

    string? FindWorktreeByPrefix(string numericPrefix)
    {
        // Prefer an exact "<prefix>-*" match. Falls through to null if WorktreeRoot doesn't
        // exist or has no matching child.
        if (!_fs.DirectoryExists(_options.WorktreeRoot))
            return null;

        return _fs.EnumerateDirectories(_options.WorktreeRoot, $"{numericPrefix}-*")
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    BranchInfo? LogUnsupportedAndReturnNull(GhNotification n)
    {
        _logger.LogWarning(
            "unsupported subject type {Type} thread={ThreadId}", n.Subject.Type, n.Id);
        return null;
    }

    /// <summary>
    /// Pulls the trailing integer off URLs like
    /// <c>https://api.github.com/repos/x/y/issues/123</c> →&#160;<c>123</c>.
    /// </summary>
    public static int? ParseLastSegmentInt(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmed = url.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0 || slash == trimmed.Length - 1)
            return null;

        return int.TryParse(trimmed[(slash + 1)..], out var n) ? n : null;
    }

    /// <summary>"16119-isdpvirtualproperty" → "16119"; "feature/foo" → null.</summary>
    static string? LeadingNumericPrefix(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsDigit(s[i]))
            i++;
        return i > 0 ? s[..i] : null;
    }
}
