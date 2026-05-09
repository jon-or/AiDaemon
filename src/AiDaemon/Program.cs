using AiDaemon;
using AiDaemon.Configuration;
using AiDaemon.Process;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);

builder.Services.AddWindowsService(o => o.ServiceName = "AiDaemon");

builder.Services.Configure<DaemonOptions>(
    builder.Configuration.GetSection(DaemonOptions.SectionName));

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

builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
