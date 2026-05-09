using AiDaemon.Configuration;

namespace AiDaemon.Tests.Configuration;

public class DaemonOptionsValidatorTests
{
    static DaemonOptions Valid() => new()
    {
        WorktreeRoot = @"D:\git\orez.worktrees",
        AiUserLogin = "jon-or-ai",
        RepoAllowlist = new() { "ownerrez/orez" },
        ActionableReasons = new() { "mention", "review_requested" },
    };

    [Fact]
    public void Validate_AllRequiredFieldsSet_Succeeds()
    {
        var result = new DaemonOptionsValidator().Validate(null, Valid());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MissingWorktreeRoot_Fails()
    {
        var opts = Valid();
        opts.WorktreeRoot = "";
        var result = new DaemonOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("WorktreeRoot"));
    }

    [Fact]
    public void Validate_MissingAiUserLogin_Fails()
    {
        var opts = Valid();
        opts.AiUserLogin = "";
        var result = new DaemonOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("AiUserLogin"));
    }

    [Fact]
    public void Validate_EmptyRepoAllowlist_Fails()
    {
        var opts = Valid();
        opts.RepoAllowlist = new();
        var result = new DaemonOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("RepoAllowlist"));
    }

    [Fact]
    public void Validate_EmptyActionableReasons_Fails()
    {
        var opts = Valid();
        opts.ActionableReasons = new();
        var result = new DaemonOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("ActionableReasons"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ZeroOrNegativePollInterval_Fails(int value)
    {
        var opts = Valid();
        opts.PollIntervalSeconds = value;
        var result = new DaemonOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("PollIntervalSeconds"));
    }

    [Fact]
    public void Validate_AggregatesAllFailures_NotJustTheFirst()
    {
        // A misconfigured deploy should hear about every problem in one go, not in a series
        // of "fix one, restart, hit the next" cycles.
        var opts = new DaemonOptions(); // every required field empty
        var result = new DaemonOptionsValidator().Validate(null, opts);
        Assert.True(result.Failed);
        Assert.True(result.Failures!.Count() >= 4); // at least Worktree, AiUserLogin, RepoAllowlist, ActionableReasons
    }
}
