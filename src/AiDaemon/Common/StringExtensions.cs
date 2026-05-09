namespace AiDaemon.Common;

static class StringExtensions
{
    /// <summary>
    /// Returns <paramref name="s"/> unchanged if it fits in <paramref name="max"/> chars,
    /// otherwise truncates to <paramref name="max"/> chars and appends a single-char ellipsis.
    /// Used in log/error messages where the underlying value is unbounded (process stdout,
    /// HTTP body, etc.).
    /// </summary>
    public static string TruncateWithEllipsis(this string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
