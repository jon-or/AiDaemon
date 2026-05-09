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

builder.Services.AddWindowsService(o => o.ServiceName = "AiDaemon");

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

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
return 0;
