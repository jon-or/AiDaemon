namespace AiDaemon.Process;

public interface IProcessRunner
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="args"/> and returns its exit code, stdout, and stderr.
    /// stdin is closed immediately so any unexpected interactive prompt EOFs.
    /// On cancellation the entire process tree is killed and the token is rethrown.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? stdin = null,
        CancellationToken cancellationToken = default);
}
