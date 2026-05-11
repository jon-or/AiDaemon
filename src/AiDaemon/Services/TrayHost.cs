using System.Diagnostics;
using AiDaemon.Common;
using AiDaemon.Configuration;
using AiDaemon.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiDaemon.Services;

/// <summary>
/// System-tray UI for the daemon. Runs only when the host has an interactive desktop
/// session (the scheduled-task install path); in any non-interactive context (CI, future
/// service-style invocation) <see cref="StartAsync"/> short-circuits and the daemon
/// behaves identically to before this was added.
///
/// Threading: WinForms requires a single-threaded apartment with a running message pump.
/// We own that thread here so the rest of the host stays free-threaded — the BackgroundService
/// worker never touches UI primitives. Menu actions that need async work hand back to the
/// thread pool via <c>Task.Run</c> and marshal completion back via <see cref="SynchronizationContext"/>.
/// </summary>
public class TrayHost : IHostedService, IDisposable
{
    readonly IServiceProvider _services;
    readonly IOptions<DaemonOptions> _options;
    readonly IHostApplicationLifetime _lifetime;
    readonly ILogger<TrayHost> _logger;

    Thread? _uiThread;
    ManualResetEventSlim? _uiReady;
    ApplicationContext? _appContext;
    NotifyIcon? _notifyIcon;
    SynchronizationContext? _uiContext;
    System.Drawing.Icon? _iconActive;
    System.Drawing.Icon? _iconPaused;
    ToolStripMenuItem? _pauseItem;
    ToolStripMenuItem? _retryRoot;
    ToolStripLabel? _statusLabel;

    public TrayHost(
        IServiceProvider services,
        IOptions<DaemonOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<TrayHost> logger)
    {
        // We take IServiceProvider rather than the individual services so async menu
        // handlers can resolve scoped DI usage on demand; the singleton services (state
        // store, processor, gh client) would work fine as direct deps, but the indirection
        // keeps StartAsync's surface narrow and avoids a constructor-injection cycle if
        // anything in this chain later depends back on the host.
        _services = services;
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Environment.UserInteractive)
        {
            _logger.LogInformation("TrayHost skipped: Environment.UserInteractive is false (no desktop session).");
            return Task.CompletedTask;
        }

        _uiReady = new ManualResetEventSlim(false);
        _uiThread = new Thread(UiThreadMain)
        {
            IsBackground = true,
            Name = "AiDaemon-Tray",
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        // Block briefly so StopAsync (or an early failure) sees a fully-built UI rather
        // than a half-constructed NotifyIcon. The pump starts inside UiThreadMain — if it
        // doesn't signal within 5s something is structurally wrong and we'd rather see the
        // log line than silently swallow it.
        if (!_uiReady.Wait(TimeSpan.FromSeconds(5), cancellationToken))
        {
            _logger.LogWarning("TrayHost UI thread did not signal ready within 5s — continuing without tray.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ExitThread posts a WM_QUIT to the pump; the thread unwinds Application.Run and
            // we then dispose the NotifyIcon. If we Dispose without ExitThread first the
            // pump keeps the process alive past host shutdown.
            if (_appContext != null)
            {
                _appContext.ExitThread();
            }

            _uiThread?.Join(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrayHost shutdown encountered an error — continuing.");
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        // NotifyIcon leaves a ghost icon in the tray until you hover over it if Visible
        // isn't cleared first. The order matters: Visible=false → Dispose → icon handles.
        try
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }
        catch { /* shutdown best-effort */ }

        _iconActive?.Dispose();
        _iconPaused?.Dispose();
        _appContext?.Dispose();
        _uiReady?.Dispose();
    }

    // --------------------------- UI thread ---------------------------

    void UiThreadMain()
    {
        try
        {
            ApplicationConfiguration.Initialize();

            _iconActive = LoadIcon("app.ico");
            _iconPaused = LoadIcon("app-paused.ico");

            var menu = BuildContextMenu();

            _notifyIcon = new NotifyIcon
            {
                Icon = _iconActive,
                Visible = true,
                Text = "AiDaemon",
                ContextMenuStrip = menu,
            };
            // Left-click opens the menu too — matches what most tray-icon apps do.
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMenu();
            };
            menu.Opening += (_, _) => RefreshDynamicMenuItems();

            _appContext = new ApplicationContext();

            // ApplicationConfiguration.Initialize installs a WindowsFormsSynchronizationContext
            // on this thread; capture it so background-thread balloon notifications can Post
            // back to the UI thread without owning a Control.
            _uiContext = SynchronizationContext.Current;

            _uiReady!.Set();

            Application.Run(_appContext);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrayHost UI thread crashed.");
            _uiReady?.Set();
        }
    }

    ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        _statusLabel = new ToolStripLabel("AiDaemon");
        _statusLabel.Font = new System.Drawing.Font(_statusLabel.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(_statusLabel);
        menu.Items.Add(new ToolStripSeparator());

        var showLog = new ToolStripMenuItem("Show today's log", image: null, (_, _) => OnShowLog());
        var openLogs = new ToolStripMenuItem("Open log folder", image: null, (_, _) => OnOpenLogFolder());
        menu.Items.Add(showLog);
        menu.Items.Add(openLogs);
        menu.Items.Add(new ToolStripSeparator());

        _pauseItem = new ToolStripMenuItem("Pause polling", image: null, (_, _) => OnTogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());

        _retryRoot = new ToolStripMenuItem("Retry");
        _retryRoot.DropDownItems.Add(new ToolStripMenuItem("(no recent items)") { Enabled = false });
        menu.Items.Add(_retryRoot);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("Quit", image: null, (_, _) => OnQuit()));

        return menu;
    }

    /// <summary>
    /// Called every time the context menu opens. Cheap items (Pause label, status text, icon
    /// tint) refresh synchronously; the Retry submenu hits SQLite synchronously too — 20 rows
    /// from a local file is sub-millisecond and not worth the marshalling complexity of a
    /// background-load placeholder.
    /// </summary>
    void RefreshDynamicMenuItems()
    {
        var paused = File.Exists(PausedFlagPath());
        if (_pauseItem != null)
            _pauseItem.Text = paused ? "Resume polling" : "Pause polling";

        if (_statusLabel != null)
            _statusLabel.Text = paused ? "AiDaemon (paused)" : "AiDaemon";

        if (_notifyIcon != null)
            _notifyIcon.Icon = paused ? _iconPaused : _iconActive;

        RebuildRetrySubmenu();
    }

    void RebuildRetrySubmenu()
    {
        if (_retryRoot == null) return;

        IReadOnlyList<ProcessedEntry> entries;
        try
        {
            // Sync-over-async on a local SQLite read is fine; this is the UI thread but
            // the query is sub-millisecond against a WAL-mode file. Avoiding it would
            // require a load-on-hover state machine that's not worth the complexity.
            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
            entries = store.ListRecentProcessedAsync(20, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrayHost failed to load processed entries for Retry submenu.");
            _retryRoot.DropDownItems.Clear();
            _retryRoot.DropDownItems.Add(new ToolStripMenuItem("(load failed — see log)") { Enabled = false });
            return;
        }

        _retryRoot.DropDownItems.Clear();
        if (entries.Count == 0)
        {
            _retryRoot.DropDownItems.Add(new ToolStripMenuItem("(no recent items)") { Enabled = false });
            return;
        }

        foreach (var entry in entries)
        {
            // Captured-locally for the closure: ToolStripMenuItem's Click fires on the UI
            // thread later, by which point the iteration variable has advanced. We need a
            // per-iteration copy.
            var local = entry;
            var item = new ToolStripMenuItem(FormatEntryLabel(local), image: null, (_, _) => OnRetryClicked(local));
            item.ToolTipText = FormatEntryTooltip(local);
            _retryRoot.DropDownItems.Add(item);
        }
    }

    static string FormatEntryLabel(ProcessedEntry e)
    {
        // "owner/repo — title (outcome, 12m ago)" with truncation so the menu doesn't widen
        // for one chatty notification. Repo + outcome are the operator's primary signals;
        // the title is helpful but secondary.
        var repo = string.IsNullOrEmpty(e.Repo) ? "(repo?)" : e.Repo;
        var title = string.IsNullOrEmpty(e.Title) ? $"thread {e.ThreadId}" : e.Title;
        if (title.Length > 60) title = title[..60] + "…";

        var age = HumanAge(DateTimeOffset.UtcNow - e.ProcessedAt);
        var outcome = string.IsNullOrEmpty(e.Outcome) ? "?" : e.Outcome;
        if (outcome.Length > 24) outcome = outcome[..24] + "…";

        return $"{repo} — {title}  ({outcome}, {age} ago)";
    }

    static string FormatEntryTooltip(ProcessedEntry e)
        => $"thread {e.ThreadId} / comment {e.CommentId}\n" +
           $"outcome: {e.Outcome}\n" +
           $"processed: {e.ProcessedAt.ToLocalTime():yyyy-MM-dd HH:mm}";

    static string HumanAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60)  return $"{(int)age.TotalSeconds}s";
        if (age.TotalMinutes < 60)  return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 48)    return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    void ShowMenu()
    {
        // Public ContextMenuStrip API doesn't expose a Show-at-cursor entry; the standard
        // trick is to invoke the private ShowContextMenu via reflection. Safer alternative:
        // call Show with Cursor.Position directly — works since .NET 6.
        _notifyIcon?.ContextMenuStrip?.Show(System.Windows.Forms.Cursor.Position);
    }

    // --------------------------- menu actions ---------------------------

    void OnShowLog()
    {
        var logPath = Path.Combine(LogDir(), $"ai-daemon-{DateTime.Now:yyyyMMdd}.log");
        if (!File.Exists(logPath))
        {
            _logger.LogInformation("TrayHost Show Log: today's log {Path} doesn't exist yet.", logPath);
            _notifyIcon?.ShowBalloonTip(
                3000, "AiDaemon", $"No log yet at {logPath}", ToolTipIcon.Info);
            return;
        }

        try
        {
            // -NoExit keeps the window open after Ctrl+C; -Tail 80 gives instant context.
            // -Wait makes it follow new writes (no need to manually re-run).
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -NoProfile -Command \"Get-Content -LiteralPath '{logPath.Replace("'", "''")}' -Wait -Tail 80\"",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrayHost Show Log failed to spawn powershell.");
        }
    }

    void OnOpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", LogDir()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrayHost Open Log Folder failed.");
        }
    }

    void OnTogglePause()
    {
        var path = PausedFlagPath();
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("TrayHost cleared PAUSED flag at {Path}", path);
                _notifyIcon?.ShowBalloonTip(2000, "AiDaemon", "Polling resumed.", ToolTipIcon.Info);
            }
            else
            {
                // Empty file is enough — Worker checks existence, not contents.
                File.WriteAllText(path, "");
                _logger.LogInformation("TrayHost set PAUSED flag at {Path}", path);
                _notifyIcon?.ShowBalloonTip(2000, "AiDaemon", "Polling paused.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TrayHost toggle pause failed for {Path}", path);
            _notifyIcon?.ShowBalloonTip(3000, "AiDaemon", $"Toggle failed: {ex.Message}", ToolTipIcon.Warning);
        }

        RefreshDynamicMenuItems();
    }

    void OnRetryClicked(ProcessedEntry entry)
    {
        // Heavy lifting (gh fetch + pipeline) on a background task so the menu closes
        // immediately. We capture a snapshot of services for the closure; the host's
        // root provider is alive until shutdown so the scope outlives the await.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _services.CreateScope();
                var gh = scope.ServiceProvider.GetRequiredService<IGhClient>();
                var store = scope.ServiceProvider.GetRequiredService<IStateStore>();
                var processor = scope.ServiceProvider.GetRequiredService<INotificationProcessor>();

                var ct = _lifetime.ApplicationStopping;

                var fresh = await gh.GetNotificationThreadAsync(entry.ThreadId, ct);
                if (fresh == null)
                {
                    _logger.LogInformation("Retry: thread {ThreadId} no longer fetchable from GitHub (404/expired).", entry.ThreadId);
                    ShowBalloonOnUiThread("Retry skipped",
                        $"Thread {entry.ThreadId} is no longer available from GitHub.",
                        ToolTipIcon.Warning);
                    return;
                }

                var removed = await store.UnmarkProcessedAsync(entry.ThreadId, entry.CommentId, ct);
                if (!removed)
                {
                    _logger.LogDebug("Retry: dedup row for {ThreadId}/{CommentId} was already gone.",
                        entry.ThreadId, entry.CommentId);
                }

                var outcome = await processor.ProcessOneAsync(fresh, ct);
                _logger.LogInformation("Retry outcome for thread {ThreadId}: {Outcome}", entry.ThreadId, outcome);

                ShowBalloonOnUiThread("Retry " + outcome,
                    $"{fresh.Repository.FullName} — {fresh.Subject.Title}",
                    outcome is RetryOutcome.Failed or RetryOutcome.Unresolved
                        ? ToolTipIcon.Warning
                        : ToolTipIcon.Info);
            }
            catch (OperationCanceledException)
            {
                // Host shutting down mid-retry: nothing to surface.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retry failed for thread {ThreadId}", entry.ThreadId);
                ShowBalloonOnUiThread("Retry failed", ex.Message, ToolTipIcon.Error);
            }
        });
    }

    void OnQuit()
    {
        _logger.LogInformation("TrayHost: operator clicked Quit — stopping host.");
        _lifetime.StopApplication();
    }

    // --------------------------- helpers ---------------------------

    System.Drawing.Icon LoadIcon(string name)
    {
        using var stream = EmbeddedResource.OpenStream(typeof(TrayHost).Assembly, name);
        // System.Drawing.Icon(Stream) copies the bytes, so disposing the stream here is safe.
        return new System.Drawing.Icon(stream);
    }

    string LogDir() => Path.Combine(_options.Value.DataDir, "logs");

    string PausedFlagPath() => Path.Combine(_options.Value.DataDir, "PAUSED");

    void ShowBalloonOnUiThread(string title, string text, ToolTipIcon icon)
    {
        // NotifyIcon is a Component (no Invoke/BeginInvoke); we marshal via the captured
        // WindowsFormsSynchronizationContext instead. ShowBalloonTip needs the UI thread
        // because it pokes USER32 from inside the NotifyIcon's hidden message-pump window.
        var ctx = _uiContext;
        if (ctx == null) return;

        try
        {
            ctx.Post(_ => _notifyIcon?.ShowBalloonTip(3500, title, text, icon), null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TrayHost balloon marshal failed (host probably shutting down).");
        }
    }
}
