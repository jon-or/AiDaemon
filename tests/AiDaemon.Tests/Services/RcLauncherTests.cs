using AiDaemon.Services;

namespace AiDaemon.Tests.Services;

/// <summary>
/// Pure-function and registry-parsing coverage for <see cref="RcLauncher"/>. The
/// PowerShell / WMI flow itself is integration territory; this file pins the boundary
/// behaviors that don't need a real process.
/// </summary>
public class RcLauncherTests
{
    [Theory]
    [InlineData("16119-isdpvirtualproperty", "16119-isdpvirtualproperty")]
    // Slashes preserved — feature branches commonly use them.
    [InlineData("feature/foo", "feature/foo")]
    // Dots and underscores allowed.
    [InlineData("release/v1.2.3", "release/v1.2.3")]
    [InlineData("a_b", "a_b")]
    // Shell-special characters get replaced with '-'.
    [InlineData("16119; rm -rf /", "16119--rm--rf-/")]
    [InlineData("foo&bar", "foo-bar")]
    [InlineData("foo|bar", "foo-bar")]
    [InlineData("foo`evil`", "foo-evil")]
    [InlineData("$(whoami)", "whoami")]
    [InlineData("foo'bar", "foo-bar")]
    [InlineData("foo\"bar", "foo-bar")]
    [InlineData("foo bar", "foo-bar")]
    [InlineData("foo>bar<baz", "foo-bar-baz")]
    // Empty / all-stripped fall back to a stable default.
    [InlineData("", "ai-daemon")]
    [InlineData("$$$", "ai-daemon")]
    public void SafeRcName_StripsShellSpecialsViaAllowlist(string input, string expected)
    {
        Assert.Equal(expected, RcLauncher.SafeRcName(input));
    }
}
