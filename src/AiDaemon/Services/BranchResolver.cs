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
                _logger.LogInformation(
                    "no worktree for PR {Repo}#{Pr} branch={Branch} (looked under {WorktreeRoot})",
                    n.Repository.FullName, prNumber, branch, _options.WorktreeRoot);
                return null;
            }
        }

        if (!await ConfirmWorktreeOnBranchAsync(worktree, branch, n.Id, cancellationToken))
            return null;

        return new BranchInfo(n.Repository.FullName, branch, worktree, PrNumber: prNumber, IssueNumber: null);
    }

    async Task<BranchInfo?> ResolveIssueAsync(GhNotification n, int issueNumber, CancellationToken cancellationToken)
    {
        var worktree = FindWorktreeByPrefix(issueNumber.ToString());
        if (worktree == null)
        {
            _logger.LogInformation(
                "no worktree for issue {Repo}#{Issue} (looked under {WorktreeRoot}\\{Issue}-*)",
                n.Repository.FullName, issueNumber, _options.WorktreeRoot, issueNumber);
            return null;
        }

        var branch = await ReadCurrentBranchAsync(worktree, cancellationToken);
        if (string.IsNullOrWhiteSpace(branch))
        {
            _logger.LogWarning("git rev-parse failed in {Worktree} thread={ThreadId}", worktree, n.Id);
            return null;
        }

        return new BranchInfo(n.Repository.FullName, branch, worktree, PrNumber: null, IssueNumber: issueNumber);
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(GitTimeout);

        try
        {
            var result = await _runner.RunAsync(
                "git",
                new[] { "-C", worktree, "rev-parse", "--abbrev-ref", "HEAD" },
                cancellationToken: cts.Token);

            return result.Succeeded ? result.Stdout.Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "git rev-parse in {Worktree} did not return within {Timeout}s — treating as unresolved",
                worktree, GitTimeout.TotalSeconds);
            return null;
        }
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
