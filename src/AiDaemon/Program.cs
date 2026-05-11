using System.Runtime.InteropServices;
using AiDaemon;
using AiDaemon.Configuration;
using AiDaemon.Io;
using AiDaemon.Process;
using AiDaemon.Services;
using AiDaemon.Storage;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

// One-shot subcommands for debugging. Run synchronously and exit.
if (args.Length >= 1 && args[0] == "set-kv")
{
    // The exe is built as <OutputType>WinExe</OutputType> so the scheduled-task launch at
    // logon doesn't pop a console window. That means Console.Out is a null sink unless we
    // explicitly attach to a parent's console -- which is exactly what the operator wants
    // when they run `AiDaemon.exe set-kv ...` from a PowerShell prompt. AttachConsole
    // returns false (and we ignore it) when there's no parent console; in that case the
    // output silently goes nowhere but the underlying SetKvAsync still runs and exit code
    // is the source of truth.
    NativeMethods.AttachConsole(NativeMethods.AttachParentProcess);

    if (args.Length != 3)
    {
        Console.Error.WriteLine("usage: AiDaemon set-kv <key> <value>");
        return 2;
    }

    var opts = builder.Configuration
        .GetSection(DaemonOptions.SectionName)
        .Get<DaemonOptions>() ?? new DaemonOptions();

    var store = new SqliteStateStore(
        Microsoft.Extensions.Options.Options.Create(opts),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteStateStore>.Instance);

    await store.InitializeAsync(default);
    await store.SetKvAsync(args[1], args[2], default);
    Console.WriteLine($"set {args[1]} = {args[2]}");
    return 0;
}

builder.Services.AddOptions<DaemonOptions>()
    .Bind(builder.Configuration.GetSection(DaemonOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<DaemonOptions>, DaemonOptionsValidator>();

var bootOptions = builder.Configuration
    .GetSection(DaemonOptions.SectionName)
    .Get<DaemonOptions>() ?? new DaemonOptions();

Directory.CreateDirectory(Path.Combine(bootOptions.DataDir, "logs"));

builder.Services.AddSerilog((sp, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(bootOptions.DataDir, "logs", "ai-daemon-.log"),
        rollingInterval: RollingInterval.Day,
        rollOnFileSizeLimit: true,
        fileSizeLimitBytes: 50_000_000,
        retainedFileCountLimit: 14,
        shared: false));

builder.Services.AddSingleton<IFileSystem, FileSystem>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IStateStore, SqliteStateStore>();
builder.Services.AddSingleton<IGhClient, GhClient>();
builder.Services.AddSingleton<IClaudeRunner, ClaudeRunner>();
builder.Services.AddSingleton<ITriagePipeline, TriagePipeline>();
builder.Services.AddSingleton<IBranchResolver, BranchResolver>();
builder.Services.AddSingleton<INotificationPoller, NotificationPoller>();
builder.Services.AddSingleton<IRcLauncher, RcLauncher>();
builder.Services.AddSingleton<IAgentPreRunner, AgentPreRunner>();
// AddHttpClient<TInterface, TClient> registers NtfyPusher as transient, but the singleton
// Dispatcher captures one instance for its lifetime — fine here because the daemon is a
// worker process where DNS-rotation concerns don't apply, and HttpClientFactory still
// manages handler pooling.
builder.Services.AddHttpClient<INotificationPusher, NtfyPusher>();
builder.Services.AddSingleton<IDispatcher, Dispatcher>();
builder.Services.AddSingleton<INotificationProcessor, NotificationProcessor>();

builder.Services.AddHostedService<Worker>();
// TrayHost runs after Worker — when the host shuts down, hosted services stop in reverse
// registration order, so the tray UI tears down BEFORE the worker (giving Quit-from-tray
// time to propagate via IHostApplicationLifetime). Inside non-interactive contexts the
// hosted service short-circuits via Environment.UserInteractive.
builder.Services.AddHostedService<TrayHost>();

var host = builder.Build();
host.Run();
return 0;

/// <summary>
/// kernel32 P/Invokes. Only used by the subcommand path -- the long-running daemon never
/// touches a console because the tray icon is its UI surface. We use the classic DllImport
/// here rather than LibraryImport so we don't have to flip AllowUnsafeBlocks on the whole
/// project just for a single bool-returning entry point.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Sentinel for AttachConsole that means "the parent process's console".</summary>
    internal const uint AttachParentProcess = unchecked((uint)-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint dwProcessId);
}
