using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SysProcess = System.Diagnostics.Process;

namespace AiDaemon.Process;

public class ProcessRunner : IProcessRunner
{
    readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? stdin = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;

        if (environment != null)
        {
            foreach (var kv in environment)
            {
                if (kv.Value == null)
                    psi.Environment.Remove(kv.Key);
                else
                    psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = new SysProcess { StartInfo = psi, EnableRaisingEvents = true };

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName}");

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            if (stdin != null)
                await proc.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken);
        }
        finally
        {
            proc.StandardInput.Close();
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            throw;
        }

        return new ProcessResult(proc.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    void TryKillTree(SysProcess proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill process tree for PID {Pid}", SafePid(proc));
        }
    }

    static int SafePid(SysProcess proc)
    {
        try { return proc.Id; } catch { return -1; }
    }
}
