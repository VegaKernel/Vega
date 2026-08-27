using System.Collections.Concurrent;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Vega.TerminalUI;

public sealed class TerminalUiHost : IDisposable
{
    private const string VegaSchemeName = "VegaDark";
    private const double TargetTicksPerSecond = 60d;
    private const double MinimumHealthyTicksPerSecond = TargetTicksPerSecond * 0.95d;
    private readonly OperationsDashboardViewModel viewModel;
    private readonly ICommandCatalog? commandCatalog;
    private readonly IOperationsLogger logger;
    private readonly IStartupProgressReader startupProgress;
    private readonly TerminalUiLayoutStore layoutStore;
    private readonly TerminalUiLayoutOptions layout;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ManualResetEventSlim initialized = new(false);
    private Thread? uiThread;
    private int running;

    public TerminalUiHost(
        IOperationsSnapshotProvider snapshots,
        IOperationsCommandService commands,
        IOperationsLogger logger,
        IStartupProgressReader? startupProgress = null,
        IUnhandledOperationsCommandSink? unhandledCommands = null,
        ICommandCatalog? commandCatalog = null,
        string? layoutPath = null)
    {
        var adminClient = new EmbeddedOperationsAdminClient(snapshots, commands, commandCatalog);
        viewModel = new OperationsDashboardViewModel(
            snapshots,
            new AdminOperationsCommandServiceAdapter(adminClient));
        this.commandCatalog = commandCatalog is null
            ? null
            : new AdminCommandCatalogAdapter(adminClient);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.startupProgress = startupProgress ?? WaitingStartupProgressReader.Instance;
        layoutStore = new TerminalUiLayoutStore(
            layoutPath ?? Path.Combine(AppContext.BaseDirectory, "config", "tui.json"),
            this.logger);
        layout = layoutStore.Load();
    }

    public bool IsRunning => Volatile.Read(ref running) != 0;

    public event Action<int>? Started;

    public event Action<bool>? Stopped;

    public bool TryStart(OperationsUiMode mode)
    {
        if (mode != OperationsUiMode.Terminal || uiThread is not null)
        {
            return false;
        }

        try
        {
            uiThread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Vega Terminal UI"
            };
            uiThread.Start();
            initialized.Wait(TimeSpan.FromSeconds(5));
            return IsRunning;
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "terminal-ui",
                "Terminal UI could not be started; the server will continue in plain mode.",
                ex);
            return false;
        }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        if (uiThread is { IsAlive: true } && Thread.CurrentThread != uiThread)
        {
            uiThread.Join(TimeSpan.FromSeconds(2));
        }

        initialized.Dispose();
        shutdown.Dispose();
    }

    private void Run()
    {
        OperationsDashboardView? dashboard = null;
        try
        {
            using IApplication app = Application.Create();
            if (OperatingSystem.IsWindows())
            {
                app.ForceDriver = DriverRegistry.Names.WINDOWS;
            }

            app.Init();
            RegisterColorScheme();
            Volatile.Write(ref running, 1);
            Started?.Invoke(Environment.CurrentManagedThreadId);
            initialized.Set();
            using Window window = new()
            {
                Title = "Vega Operations",
                SchemeName = VegaSchemeName
            };
            var startup = new FrameView
            {
                Title = "Starting server",
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = 9,
                SchemeName = VegaSchemeName
            };
            var brand = new Label
            {
                Text = "V E G A   S E R V E R",
                X = Pos.Center(),
                Y = 1
            };
            var startupStage = new Label
            {
                Text = "Loading Terraria world",
                X = 1,
                Y = 3,
                Width = Dim.Fill(1)
            };
            var progress = new ProgressBar
            {
                X = 1,
                Y = 4,
                Width = Dim.Fill(1),
                ProgressBarStyle = ProgressBarStyle.MarqueeContinuous,
                SyncWithTerminal = true
            };
            var startupDetails = new Label
            {
                Text = "World selected · initializing runtime",
                X = 1,
                Y = 6,
                Width = Dim.Fill(1)
            };
            startup.Add(brand, startupStage, progress, startupDetails);

            var desktop = new View
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(5),
                SchemeName = VegaSchemeName,
                Visible = false
            };
            var results = new ConcurrentQueue<string>();
            dashboard = new OperationsDashboardView(desktop, layout);
            TerminalUiFixedLayout.Apply(desktop);
            var terminalSizeWarning = new Label
            {
                Text = "Terminal too small for dashboard. Resize to at least 72x22.",
                X = Pos.Center(),
                Y = Pos.Center(),
                Visible = false,
                SchemeName = VegaSchemeName
            };

            var commandBar = new FrameView
            {
                Title = "Command palette",
                X = 0,
                Y = Pos.AnchorEnd(5),
                Width = Dim.Fill(),
                Height = 4,
                SchemeName = VegaSchemeName,
                Visible = false
            };
            var prompt = new Label
            {
                X = 1,
                Y = 0,
                Text = "Command or :windows / :show / :hide / :layout save|reset"
            };
            var command = new TextField
            {
                X = 1,
                Y = 1,
                Width = Dim.Percent(48),
                Text = string.Empty
            };
            var commandSuggestions = new SingleWordSuggestionGenerator();
            if (command.Autocomplete is { } autocomplete)
            {
                autocomplete.SuggestionGenerator = commandSuggestions;
                autocomplete.SelectionKey = Key.Tab;
                autocomplete.MaxWidth = 48;
                autocomplete.MaxHeight = 8;
            }

            string[] lastCommandSuggestions = Array.Empty<string>();

            void RefreshCommandSuggestions()
            {
                if (commandCatalog is null)
                {
                    return;
                }

                string[] suggestions = commandCatalog
                    .SnapshotCommands()
                    .SelectMany(static descriptor => descriptor.Aliases.Prepend(descriptor.Name))
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (lastCommandSuggestions.SequenceEqual(suggestions, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                lastCommandSuggestions = suggestions;
                commandSuggestions.AllSuggestions = suggestions.ToList();
            }

            RefreshCommandSuggestions();
            var result = new Label
            {
                X = Pos.Right(command) + 2,
                Y = 1,
                Width = Dim.Fill(1),
                Text = "Ready. F1 shows keyboard help."
            };
            bool dashboardVisible = false;
            OperationsSnapshot? latestSnapshot = null;

            void FocusCommandPalette()
            {
                if (dashboardVisible)
                {
                    command.SetFocus();
                }
            }

            void FocusLogs()
            {
                if (dashboardVisible)
                {
                    dashboard.FocusWindow(TerminalUiLayoutOptions.Logs);
                }
            }

            void ShowAdministration()
            {
                if (dashboardVisible && latestSnapshot is not null)
                {
                    AdministrationBrowser.Show(app, latestSnapshot.Administration);
                }
            }

            void ToggleFollowing()
            {
                if (!dashboardVisible)
                {
                    return;
                }

                results.Enqueue(viewModel.ToggleFollowing()
                    ? "Log following enabled."
                    : "Log following paused; telemetry continues updating.");
            }

            void ClearFilters()
            {
                if (!dashboardVisible)
                {
                    return;
                }

                viewModel.ClearLogFilters();
                results.Enqueue("Log filters cleared.");
            }

            void SaveLayout()
            {
                if (!dashboardVisible)
                {
                    return;
                }

                results.Enqueue(layoutStore.TrySave(dashboard.CaptureLayout())
                    ? "Window layout saved."
                    : "Window layout could not be saved; see logs.");
            }

            void ResetLayout()
            {
                if (!dashboardVisible)
                {
                    return;
                }

                dashboard.ResetLayout();
                SaveLayout();
            }

            void ShowHelp()
            {
                MessageBox.Query(
                    app,
                    "Vega keyboard",
                    "F1 Help\nF2 Focus logs\nF3 Log filter\nF4 Administration search\nF5 Follow / pause logs\nCtrl+P Command palette\nTab Accept command suggestion / move focus\nDrag panel borders Resize adjacent panels\nDouble-click title Maximize / restore\nRight-click Players / Modules Actions\nF10 Main menu\n\nDashboard panels are tiled: they resize but do not move or overlap. Logs pause never freezes TPS, players, network or graphs.",
                    "OK");
            }

            void ShowLogFilter()
            {
                if (dashboardVisible)
                {
                    ShowLogFilterDialog(app);
                }
            }

            dashboard.LogFilterRequested += ShowLogFilter;
            dashboard.LogFollowToggleRequested += ToggleFollowing;
            dashboard.LogFiltersClearRequested += ClearFilters;
            dashboard.CommandRequested += input => _ = ExecuteAsync(input, results, dashboard);

            command.Accepting += (_, eventArgs) =>
            {
                string input = command.Text;
                command.Text = string.Empty;
                eventArgs.Handled = true;
                _ = ExecuteAsync(input, results, dashboard);
            };

            commandBar.Add(prompt, command, result);

            var menuBar = CreateMenuBar(
                dashboard,
                FocusCommandPalette,
                ShowAdministration,
                SaveLayout,
                ResetLayout,
                ShowHelp,
                () => MessageBox.Query(
                    app,
                    "Vega Operations",
                    "Terminal operations dashboard for Vega.\nTarget server tick rate: 60 TPS.",
                    "OK"));
            menuBar.Visible = false;

            var statusInfo = new Shortcut
            {
                Title = "TPS --/60 · logs FOLLOW"
            };
            var statusBar = new StatusBar([
                CreateShortcut("Help", Key.F1, ShowHelp),
                CreateShortcut("Logs", Key.F2, FocusLogs),
                CreateShortcut("Filter", Key.F3, ShowLogFilter),
                CreateShortcut("Admin", Key.F4, ShowAdministration),
                CreateShortcut("Follow", Key.F5, ToggleFollowing),
                CreateShortcut("Command", Key.P.WithCtrl, FocusCommandPalette),
                statusInfo
            ])
            {
                SchemeName = VegaSchemeName,
                Visible = false
            };

            window.Add(menuBar, startup, desktop, terminalSizeWarning, commandBar, statusBar);
            DateTimeOffset lastRefreshFailureLoggedAt = DateTimeOffset.MinValue;
            Task<OperationsSnapshot>? snapshotCapture = Task.Run(viewModel.Capture);
            app.AddTimeout(TimeSpan.FromMilliseconds(layout.RefreshIntervalMilliseconds), () =>
            {
                if (shutdown.IsCancellationRequested)
                {
                    app.RequestStop(window);
                    return false;
                }

                try
                {
                    if (snapshotCapture.IsCompleted)
                    {
                        Task<OperationsSnapshot> completedCapture = snapshotCapture;
                        snapshotCapture = Task.Run(viewModel.Capture);
                        latestSnapshot = viewModel.Apply(
                            completedCapture.GetAwaiter().GetResult());
                    }

                    RefreshCommandSuggestions();
                    if (latestSnapshot is not OperationsSnapshot snapshot)
                    {
                        StartupProgressSnapshot current = startupProgress.Read();
                        startupStage.Text = current.Activity;
                        progress.Pulse();
                        return true;
                    }

                    if (!snapshot.Server.IsNetworkReady)
                    {
                        StartupProgressSnapshot current = startupProgress.Read();
                        startupStage.Text = current.Activity;
                        startupDetails.Text = $"{snapshot.Server.LifecycleStage} · elapsed {snapshot.Server.Uptime:mm\\:ss\\.f}";
                        if (current.Fraction is double fraction)
                        {
                            progress.ProgressBarStyle = ProgressBarStyle.Blocks;
                            progress.ProgressBarFormat = ProgressBarFormat.SimplePlusPercentage;
                            progress.Fraction = (float)fraction;
                        }
                        else
                        {
                            progress.ProgressBarStyle = ProgressBarStyle.MarqueeContinuous;
                            progress.ProgressBarFormat = ProgressBarFormat.Simple;
                            progress.Pulse();
                        }
                    }
                    else
                    {
                        if (!dashboardVisible)
                        {
                            dashboardVisible = true;
                            startup.Visible = false;
                            menuBar.Visible = true;
                            statusBar.Visible = true;
                        }

                        bool usableSize = IsTerminalSizeUsable(window.Viewport.Width, window.Viewport.Height);
                        terminalSizeWarning.Visible = !usableSize;
                        desktop.Visible = usableSize;
                        commandBar.Visible = usableSize;
                        if (usableSize)
                        {
                            dashboard.Update(snapshot, viewModel.IsFollowing, viewModel.LogFilter);
                            statusInfo.Title = BuildStatus(snapshot, viewModel.IsFollowing, viewModel.LogFilter);
                            if (!command.HasFocus && window.Focused is null)
                            {
                                command.SetFocus();
                            }
                        }
                        else
                        {
                            statusInfo.Title = $"Terminal {window.Viewport.Width}x{window.Viewport.Height} · resize to >=72x22";
                        }
                    }

                    while (results.TryDequeue(out string? message))
                    {
                        result.Text = message;
                    }
                }
                catch (Exception ex)
                {
                    results.Enqueue($"UI refresh recovered: {ex.Message}");
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    if (now - lastRefreshFailureLoggedAt >= TimeSpan.FromSeconds(5))
                    {
                        lastRefreshFailureLoggedAt = now;
                        logger.Log(
                            OperationsLogLevel.Warning,
                            "terminal-ui",
                            "Terminal UI refresh failed and was recovered; the server and UI loop remain running.",
                            ex);
                    }
                }

                return true;
            });
            app.Run(window);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "terminal-ui",
                "Terminal UI initialization failed; server startup continues without it.",
                ex);
        }
        finally
        {
            if (dashboard is not null)
            {
                layoutStore.TrySave(dashboard.CaptureLayout());
                dashboard.Dispose();
            }

            Volatile.Write(ref running, 0);
            initialized.Set();
            Stopped?.Invoke(!shutdown.IsCancellationRequested);
        }
    }

    private async Task ExecuteAsync(
        string input,
        ConcurrentQueue<string> results,
        OperationsDashboardView dashboard)
    {
        try
        {
            if (dashboard.TryExecuteLayoutCommand(input, out string layoutMessage, out bool saveRequested))
            {
                if (saveRequested)
                {
                    layoutStore.TrySave(dashboard.CaptureLayout());
                }

                results.Enqueue(layoutMessage);
                return;
            }

            results.Enqueue(await viewModel
                .ExecutePaletteAsync(input, shutdown.Token)
                .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            results.Enqueue($"Error: {ex.Message}");
            logger.Log(OperationsLogLevel.Error, "terminal-ui", "Command palette action failed.", ex);
        }
    }

    private void ShowLogFilterDialog(IApplication app)
    {
        OperationsLogFilter filter = viewModel.LogFilter;
        using Dialog dialog = new()
        {
            Title = "Log filter",
            Width = 68,
            Height = 16,
            SchemeName = VegaSchemeName
        };
        var levelLabel = new Label { Text = "Minimum level", X = 1, Y = 1 };
        var level = new OptionSelector<LogLevelSelection>
        {
            X = 18,
            Y = 1,
            Value = ToSelection(filter.MinimumLevel)
        };
        var sourceLabel = new Label { Text = "Source", X = 1, Y = 9 };
        var source = new TextField
        {
            X = 18,
            Y = 9,
            Width = Dim.Fill(2),
            Text = filter.Source ?? string.Empty
        };
        var textLabel = new Label { Text = "Contains", X = 1, Y = 10 };
        var text = new TextField
        {
            X = 18,
            Y = 10,
            Width = Dim.Fill(2),
            Text = filter.Text ?? string.Empty
        };
        var reset = new Button { Text = "_Reset" };
        var cancel = new Button { Text = "_Cancel" };
        var apply = new Button { Text = "_Apply" };
        dialog.Buttons = [reset, cancel, apply];
        reset.Accepted += (_, _) =>
        {
            viewModel.ClearLogFilters();
            app.RequestStop(dialog);
        };
        cancel.Accepted += (_, _) => app.RequestStop(dialog);
        apply.Accepted += (_, _) =>
        {
            viewModel.SetLogFilter(FromSelection(level.Value), source.Text, text.Text);
            app.RequestStop(dialog);
        };
        dialog.Add(levelLabel, level, sourceLabel, source, textLabel, text);
        app.Run(dialog);
    }

    private static MenuBar CreateMenuBar(
        OperationsDashboardView dashboard,
        Action focusCommandPalette,
        Action showAdministration,
        Action saveLayout,
        Action resetLayout,
        Action showHelp,
        Action showAbout) =>
        new([
            new MenuBarItem("_Server", [
                new MenuItem("_Command palette", action: focusCommandPalette),
                new MenuItem("_Administration", action: showAdministration)
            ]),
            new MenuBarItem("_View", [
                new MenuItem("_Overview", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Overview); }),
                new MenuItem("_Performance / TPS", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Performance); }),
                new MenuItem("_Memory", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Memory); }),
                new MenuItem("_Network", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Network); }),
                new MenuItem("P_layers", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Players); }),
                new MenuItem("M_odules", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Modules); }),
                new MenuItem("_Regions", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Regions); }),
                new MenuItem("_Moderation", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Moderation); }),
                new MenuItem("Lo_gs", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Logs); }),
                new MenuItem("_Chat", action: () => { dashboard.ToggleWindowVisibility(TerminalUiLayoutOptions.Chat); }),
                new Line(),
                new MenuItem("_Save layout", action: saveLayout),
                new MenuItem("_Reset layout", action: resetLayout)
            ]),
            new MenuBarItem("_Help", [
                new MenuItem("_Keyboard", action: showHelp),
                new MenuItem("_About", action: showAbout)
            ])
        ])
        {
            SchemeName = VegaSchemeName
        };

    private static Shortcut CreateShortcut(string title, Key key, Action action) => new()
    {
        Title = title,
        Key = key,
        Action = action,
        BindKeyToApplication = true
    };

    internal static bool IsTerminalSizeUsable(int width, int height) => width >= 72 && height >= 22;

    private static string BuildStatus(
        OperationsSnapshot snapshot,
        bool isFollowing,
        OperationsLogFilter filter)
    {
        double current = snapshot.Server.TickRate.Current;
        string tps = snapshot.Server.OnlinePlayers == 0 && current <= 0
            ? "IDLE"
            : current > 0 && current < MinimumHealthyTicksPerSecond
                ? $"LOW {current:F1}/{TargetTicksPerSecond:F0}"
                : $"{current:F1}/{TargetTicksPerSecond:F0}";
        string level = filter.MinimumLevel?.ToString() ?? "all";
        return $"TPS {tps} · avg {snapshot.Server.TickRate.AverageOneMinute:F1} · P {snapshot.Server.OnlinePlayers}/{snapshot.Server.MaximumPlayers} · {(isFollowing ? "FOLLOW" : "PAUSED")} {level}";
    }

    private static LogLevelSelection ToSelection(OperationsLogLevel? level) => level switch
    {
        OperationsLogLevel.Trace => LogLevelSelection.Trace,
        OperationsLogLevel.Debug => LogLevelSelection.Debug,
        OperationsLogLevel.Information => LogLevelSelection.Information,
        OperationsLogLevel.Warning => LogLevelSelection.Warning,
        OperationsLogLevel.Error => LogLevelSelection.Error,
        OperationsLogLevel.Critical => LogLevelSelection.Critical,
        _ => LogLevelSelection.All
    };

    private static OperationsLogLevel? FromSelection(LogLevelSelection? selection) => selection switch
    {
        LogLevelSelection.Trace => OperationsLogLevel.Trace,
        LogLevelSelection.Debug => OperationsLogLevel.Debug,
        LogLevelSelection.Information => OperationsLogLevel.Information,
        LogLevelSelection.Warning => OperationsLogLevel.Warning,
        LogLevelSelection.Error => OperationsLogLevel.Error,
        LogLevelSelection.Critical => OperationsLogLevel.Critical,
        _ => null
    };

    private static void RegisterColorScheme()
    {
        var scheme = new Scheme
        {
            Normal = new TuiAttribute(Color.White, Color.Black),
            Focus = new TuiAttribute(Color.Black, Color.BrightCyan),
            HotNormal = new TuiAttribute(Color.BrightCyan, Color.Black),
            HotFocus = new TuiAttribute(Color.Black, Color.BrightYellow),
            Active = new TuiAttribute(Color.White, Color.DarkGray),
            Editable = new TuiAttribute(Color.White, Color.DarkGray),
            ReadOnly = new TuiAttribute(Color.White, Color.Black),
            Disabled = new TuiAttribute(Color.Gray, Color.Black)
        };
        SchemeManager.AddScheme(VegaSchemeName, scheme);
    }

    private enum LogLevelSelection
    {
        All,
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }

    private sealed class WaitingStartupProgressReader : IStartupProgressReader
    {
        public static WaitingStartupProgressReader Instance { get; } = new();

        public StartupProgressSnapshot Read() => new(
            "Loading Terraria world",
            null,
            string.Empty,
            DateTimeOffset.UtcNow);
    }
}
