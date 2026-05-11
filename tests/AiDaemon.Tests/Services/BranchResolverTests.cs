using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Models;
using AiDaemon.Process;
using AiDaemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AiDaemon.Tests.Services;

public class BranchResolverTests
{
    readonly Mock<IGhClient> _gh = new();
    readonly Mock<IFileSystem> _fs = new();
    readonly Mock<IProcessRunner> _runner = new();
    readonly DaemonOptions _options = new()
    {
        WorktreeRoot = @"C:\Users\Jon\worktrees",
        RepoAllowlist = new() { "ownerrez/orez" },
    };

    BranchResolver Build() => new(
        _gh.Object,
        _fs.Object,
        _runner.Object,
        Options.Create(_options),
        NullLogger<BranchResolver>.Instance);

    static GhNotification IssueN(int number, string repo = "ownerrez/orez") => new()
    {
        Id = "thread-issue-" + number,
        Reason = "mention",
        Repository = new GhRepositoryRef { FullName = repo },
        Subject = new GhNotificationSubject
        {
            Type = "Issue",
            Url = $"https://api.github.com/repos/{repo}/issues/{number}",
            Title = $"Issue {number}",
        },
    };

    static GhNotification PrN(int number, string repo = "ownerrez/orez") => new()
    {
        Id = "thread-pr-" + number,
        Reason = "review_requested",
        Repository = new GhRepositoryRef { FullName = repo },
        Subject = new GhNotificationSubject
        {
            Type = "PullRequest",
            Url = $"https://api.github.com/repos/{repo}/pulls/{number}",
            Title = $"PR {number}",
        },
    };

    void StubGitBranch(string worktree, string branch, int exit = 0)
    {
        _runner.Setup(r => r.RunAsync(
                "git",
                It.Is<IReadOnlyList<string>>(a =>
                    a.Count >= 5 && a[0] == "-C" && a[1] == worktree
                    && a[2] == "symbolic-ref" && a[3] == "--short" && a[4] == "HEAD"),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(exit, branch + "\n", ""));
    }

    void StubGitWorktreeAdd(string repoRoot, string worktreePath, string branch, int exit = 0, string stderr = "")
    {
        _runner.Setup(r => r.RunAsync(
                "git",
                It.Is<IReadOnlyList<string>>(a =>
                    a.Count == 6 && a[0] == "-C" && a[1] == repoRoot
                    && a[2] == "worktree" && a[3] == "add"
                    && a[4] == worktreePath && a[5] == branch),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(exit, "", stderr));
    }

    void StubGitForEachRef(string repoRoot, int issueNumber, string stdout, int exit = 0)
    {
        _runner.Setup(r => r.RunAsync(
                "git",
                It.Is<IReadOnlyList<string>>(a =>
                    a.Count == 5 && a[0] == "-C" && a[1] == repoRoot
                    && a[2] == "for-each-ref"
                    && a[3] == "--format=%(refname:short)"
                    && a[4] == $"refs/heads/{issueNumber}-*"),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(exit, stdout, ""));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/o/r/issues/123", 123)]
    [InlineData("https://api.github.com/repos/o/r/pulls/7", 7)]
    // Trailing slash should be tolerated.
    [InlineData("https://x.example/y/99/", 99)]
    [InlineData("", null)]
    [InlineData("https://x.example/y/abc", null)]
    public void ParseLastSegmentInt_HandlesNumericTrailingSegments(string url, int? expected)
    {
        Assert.Equal(expected, BranchResolver.ParseLastSegmentInt(url));
    }

    [Fact]
    public async Task OutOfScopeRepo_ReturnsNull()
    {
        var got = await Build().ResolveAsync(IssueN(1, repo: "other-org/secret"), default);
        Assert.Null(got);
        _gh.VerifyNoOtherCalls();
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Issue_WithMatchingWorktree_Resolves()
    {
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(new[] { worktree });
        StubGitBranch(worktree, "16119-isdpvirtualproperty");
        // No PR cross-link in this scenario (FindOpenPrNumberForBranchAsync returns null).

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.NotNull(got);
        Assert.Equal("ownerrez/orez", got!.Repo);
        Assert.Equal("16119-isdpvirtualproperty", got.Branch);
        Assert.Equal(worktree, got.Worktree);
        Assert.Equal(16119, got.IssueNumber);
        Assert.Null(got.PrNumber);
        Assert.Equal("ownerrez/orez:16119-isdpvirtualproperty", got.Key);
    }

    [Fact]
    public async Task Issue_CrossLinksOpenPr_WhenExactlyOneFound()
    {
        // Common case: an issue that has a single open PR named after it (e.g. branch
        // "16119-isdpvirtualproperty" → PR #16742). The resolver should populate both
        // PrNumber and IssueNumber so the push surfaces both Open PR + Open Issue buttons.
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(new[] { worktree });
        StubGitBranch(worktree, "16119-isdpvirtualproperty");
        _gh.Setup(g => g.FindOpenPrNumberForBranchAsync(
                "ownerrez/orez", "16119-isdpvirtualproperty", It.IsAny<CancellationToken>()))
            .ReturnsAsync(16742);

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.NotNull(got);
        Assert.Equal(16119, got!.IssueNumber);
        Assert.Equal(16742, got.PrNumber);
    }

    [Fact]
    public async Task Issue_NoCrossLink_WhenGhLookupThrows()
    {
        // gh transient failure — the issue resolution itself is fine; we just drop the
        // Open PR button rather than failing the dispatch.
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(new[] { worktree });
        StubGitBranch(worktree, "16119-isdpvirtualproperty");
        _gh.Setup(g => g.FindOpenPrNumberForBranchAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GhCliException(1, "transient", "transient"));

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.NotNull(got);
        Assert.Equal(16119, got!.IssueNumber);
        Assert.Null(got.PrNumber);
    }

    [Fact]
    public async Task Issue_NoMatchingWorktree_ReturnsNull()
    {
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "99999-*"))
            .Returns(Array.Empty<string>());

        var got = await Build().ResolveAsync(IssueN(99999), default);

        Assert.Null(got);
        _runner.VerifyNoOtherCalls(); // git never invoked
    }

    [Fact]
    public async Task Issue_WorktreeRootMissing_ReturnsNull()
    {
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(false);

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.Null(got);
    }

    [Fact]
    public async Task Issue_GitSymbolicRefFails_ReturnsNull()
    {
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(new[] { worktree });
        StubGitBranch(worktree, "", exit: 128);

        var got = await Build().ResolveAsync(IssueN(16119), default);
        Assert.Null(got);
    }

    [Fact]
    public async Task Issue_DetachedHead_ReturnsNullInsteadOfBranchMismatchWarning()
    {
        // Mid-rebase / mid-bisect: `git symbolic-ref --short HEAD` exits 128 because HEAD
        // is not a symbolic ref. The worktree is in a transitional state and the daemon
        // should skip cleanly rather than dispatch against the resolved branch or warn
        // about a "mismatch" between expectations.
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(new[] { worktree });
        // Simulate the real-world stderr; the resolver only inspects the exit code.
        _runner.Setup(r => r.RunAsync(
                "git",
                It.Is<IReadOnlyList<string>>(a => a.Contains("symbolic-ref")),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProcessResult(128, "", "fatal: ref HEAD is not a symbolic ref\n"));

        var got = await Build().ResolveAsync(IssueN(16119), default);
        Assert.Null(got);
    }

    [Fact]
    public async Task Pr_WorktreeMatchesHeadRef_Resolves()
    {
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = "16119-isdpvirtualproperty", Sha = "deadbeef" },
            });
        _fs.Setup(f => f.DirectoryExists(worktree)).Returns(true);
        StubGitBranch(worktree, "16119-isdpvirtualproperty");

        var got = await Build().ResolveAsync(PrN(16773), default);

        Assert.NotNull(got);
        Assert.Equal(16773, got!.PrNumber);
        Assert.Equal("16119-isdpvirtualproperty", got.Branch);
        Assert.Equal(worktree, got.Worktree);
        // Cross-link to the issue derived from the branch's numeric prefix — no network
        // call, just string parsing on "<issue>-<slug>".
        Assert.Equal(16119, got.IssueNumber);
    }

    [Fact]
    public async Task Pr_NoIssueCrossLink_WhenBranchHasNoNumericPrefix()
    {
        // Branch doesn't follow the "<issue>-<slug>" convention (e.g. "feature/foo") —
        // we can't derive an issue, so leave IssueNumber null.
        var worktree = @"C:\Users\Jon\worktrees\feature-foo";

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = "feature-foo", Sha = "x" },
            });
        _fs.Setup(f => f.DirectoryExists(worktree)).Returns(true);
        StubGitBranch(worktree, "feature-foo");

        var got = await Build().ResolveAsync(PrN(16773), default);

        Assert.NotNull(got);
        Assert.Equal(16773, got!.PrNumber);
        Assert.Null(got.IssueNumber);
    }

    [Fact]
    public async Task Pr_WorktreeOnDifferentBranch_ReturnsNull()
    {
        var worktree = @"C:\Users\Jon\worktrees\16119-isdpvirtualproperty";

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = "16119-isdpvirtualproperty", Sha = "x" },
            });
        _fs.Setup(f => f.DirectoryExists(worktree)).Returns(true);
        // Worktree currently on a different branch (e.g. someone force-pushed or renamed).
        StubGitBranch(worktree, "main");

        var got = await Build().ResolveAsync(PrN(16773), default);
        Assert.Null(got);
    }

    [Fact]
    public async Task Pr_FallsBackToNumericPrefixGlob_WhenExactDirNameMissing()
    {
        // head.ref is "16119-some-other-slug" but the worktree was created as "16119-original".
        var actualWorktree = @"C:\Users\Jon\worktrees\16119-original";
        var headRef = "16119-some-other-slug";

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = headRef, Sha = "x" },
            });

        // Exact-name directory does NOT exist.
        _fs.Setup(f => f.DirectoryExists(Path.Combine(_options.WorktreeRoot, headRef))).Returns(false);
        // But globbing "16119-*" finds the actual worktree.
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(new[] { actualWorktree });
        _fs.Setup(f => f.DirectoryExists(actualWorktree)).Returns(true);

        // The worktree IS checked out on the head.ref despite its dirname.
        StubGitBranch(actualWorktree, headRef);

        var got = await Build().ResolveAsync(PrN(16773), default);

        Assert.NotNull(got);
        Assert.Equal(actualWorktree, got!.Worktree);
        Assert.Equal(headRef, got.Branch);
    }

    [Fact]
    public async Task Pr_GhFetchThrows_ReturnsNull()
    {
        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GhCliException(1, "boom", "boom"));

        var got = await Build().ResolveAsync(PrN(16773), default);
        Assert.Null(got);
    }

    [Fact]
    public async Task Pr_NoWorktreeAtAll_ReturnsNull()
    {
        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = "16119-isdpvirtualproperty", Sha = "x" },
            });
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Array.Empty<string>());

        var got = await Build().ResolveAsync(PrN(16773), default);
        Assert.Null(got);
    }

    [Fact]
    public async Task UnsupportedSubjectType_ReturnsNull()
    {
        var n = new GhNotification
        {
            Id = "t",
            Reason = "mention",
            Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
            Subject = new GhNotificationSubject
            {
                Type = "Discussion",
                Url = "https://api.github.com/repos/ownerrez/orez/discussions/5",
                Title = "Discussion",
            },
        };

        var got = await Build().ResolveAsync(n, default);
        Assert.Null(got);
    }

    [Fact]
    public async Task BadSubjectUrl_ReturnsNull()
    {
        var n = new GhNotification
        {
            Id = "t",
            Reason = "mention",
            Repository = new GhRepositoryRef { FullName = "ownerrez/orez" },
            Subject = new GhNotificationSubject
            {
                Type = "Issue",
                Url = "https://api.github.com/repos/ownerrez/orez/issues/abc",
                Title = "?",
            },
        };

        var got = await Build().ResolveAsync(n, default);
        Assert.Null(got);
    }

    [Fact]
    public async Task EmptyWorktreeRoot_ReturnsNull()
    {
        _options.WorktreeRoot = "";
        var got = await Build().ResolveAsync(IssueN(16119), default);
        Assert.Null(got);
    }

    // ---- Auto-create behavior ----

    const string RepoRoot = @"D:\git\orez";

    void EnableAutoCreate() => _options.RepoRoots["ownerrez/orez"] = RepoRoot;

    [Fact]
    public async Task Pr_NoWorktree_AutoCreatesWhenBranchExistsLocally()
    {
        // PR with head.ref the user has fetched locally but never made a worktree for.
        // Resolver should shell `git worktree add` and treat the new dir like any other.
        EnableAutoCreate();
        var branch = "16119-isdpvirtualproperty";
        var worktree = Path.Combine(_options.WorktreeRoot, branch);

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = branch, Sha = "deadbeef" },
            });

        // Initial probes: neither exact-name dir nor any "16119-*" worktree exists.
        _fs.Setup(f => f.DirectoryExists(worktree)).Returns(false);
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(Array.Empty<string>());
        // RepoRoot must exist on disk before we shell git from there.
        _fs.Setup(f => f.DirectoryExists(RepoRoot)).Returns(true);

        StubGitWorktreeAdd(RepoRoot, worktree, branch);
        StubGitBranch(worktree, branch);

        var got = await Build().ResolveAsync(PrN(16773), default);

        Assert.NotNull(got);
        Assert.Equal(worktree, got!.Worktree);
        Assert.Equal(branch, got.Branch);
        Assert.Equal(16773, got.PrNumber);
        Assert.Equal(16119, got.IssueNumber);
    }

    [Fact]
    public async Task Pr_NoWorktree_SkipsSilentlyWhenBranchNotLocal()
    {
        // Fork PR or unfetched ref — `git worktree add` exits non-zero. Resolver should
        // return null without raising (silent skip; the next poll will retry if the user
        // fetches in the meantime).
        EnableAutoCreate();
        var branch = "feature/external-fork";
        var worktree = Path.Combine(_options.WorktreeRoot, branch);

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 9001, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 9001,
                Head = new PrRef { Ref = branch, Sha = "x" },
            });
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.DirectoryExists(RepoRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Array.Empty<string>());

        StubGitWorktreeAdd(RepoRoot, worktree, branch,
            exit: 128,
            stderr: "fatal: invalid reference: feature/external-fork\n");

        var got = await Build().ResolveAsync(PrN(9001), default);

        Assert.Null(got);
    }

    [Fact]
    public async Task Pr_NoWorktree_SkipsWhenRepoRootsNotConfigured()
    {
        // No RepoRoots entry for the repo → resolver preserves legacy "skip silently"
        // behavior even when WorktreeRoot is otherwise present.
        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = "16119-isdpvirtualproperty", Sha = "x" },
            });
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);

        var got = await Build().ResolveAsync(PrN(16773), default);

        Assert.Null(got);
        // git should never have been invoked — auto-create is short-circuited by config.
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Issue_NoWorktree_AutoCreatesWhenSingleLocalBranchMatches()
    {
        // No worktree for issue 16119, but exactly one local branch matches "16119-*" —
        // unambiguous, so we materialize a worktree for it.
        EnableAutoCreate();
        var branch = "16119-isdpvirtualproperty";
        var worktree = Path.Combine(_options.WorktreeRoot, branch);

        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(Array.Empty<string>());
        _fs.Setup(f => f.DirectoryExists(RepoRoot)).Returns(true);

        StubGitForEachRef(RepoRoot, 16119, stdout: branch + "\n");
        StubGitWorktreeAdd(RepoRoot, worktree, branch);
        StubGitBranch(worktree, branch);

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.NotNull(got);
        Assert.Equal(worktree, got!.Worktree);
        Assert.Equal(branch, got.Branch);
        Assert.Equal(16119, got.IssueNumber);
    }

    [Fact]
    public async Task Issue_NoWorktree_SkipsWhenNoLocalBranchMatches()
    {
        // for-each-ref returns empty — the user hasn't branched the issue yet. Skip silently.
        EnableAutoCreate();

        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "12345-*"))
            .Returns(Array.Empty<string>());
        _fs.Setup(f => f.DirectoryExists(RepoRoot)).Returns(true);

        StubGitForEachRef(RepoRoot, 12345, stdout: "");

        var got = await Build().ResolveAsync(IssueN(12345), default);

        Assert.Null(got);
    }

    [Fact]
    public async Task Issue_NoWorktree_SkipsWhenMultipleLocalBranchesMatch()
    {
        // Two stale branches with the same issue prefix → we don't guess. Skip silently.
        EnableAutoCreate();

        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(Array.Empty<string>());
        _fs.Setup(f => f.DirectoryExists(RepoRoot)).Returns(true);

        StubGitForEachRef(RepoRoot, 16119,
            stdout: "16119-isdpvirtualproperty\n16119-old-attempt\n");

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.Null(got);
        // worktree add must not be invoked when the branch is ambiguous.
        _runner.Verify(r => r.RunAsync(
                "git",
                It.Is<IReadOnlyList<string>>(a => a.Contains("worktree") && a.Contains("add")),
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string?>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Issue_NoWorktree_SkipsWhenRepoRootsNotConfigured()
    {
        // No RepoRoots entry → resolver doesn't even shell for-each-ref.
        _fs.Setup(f => f.DirectoryExists(_options.WorktreeRoot)).Returns(true);
        _fs.Setup(f => f.EnumerateDirectories(_options.WorktreeRoot, "16119-*"))
            .Returns(Array.Empty<string>());

        var got = await Build().ResolveAsync(IssueN(16119), default);

        Assert.Null(got);
        _runner.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AutoCreate_SkipsWhenRepoRootDirectoryMissing()
    {
        // RepoRoots is configured but the path doesn't exist on disk (typo, dead drive).
        // We warn (not asserted here — NullLogger swallows) and skip.
        EnableAutoCreate();

        _gh.Setup(g => g.GetPullRequestAsync("ownerrez/orez", 16773, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrInfo
            {
                Number = 16773,
                Head = new PrRef { Ref = "16119-isdpvirtualproperty", Sha = "x" },
            });
        _fs.Setup(f => f.DirectoryExists(It.IsAny<string>())).Returns(false);

        var got = await Build().ResolveAsync(PrN(16773), default);

        Assert.Null(got);
        // Never reached the for-each-ref / worktree-add subprocess.
        _runner.VerifyNoOtherCalls();
    }
}
