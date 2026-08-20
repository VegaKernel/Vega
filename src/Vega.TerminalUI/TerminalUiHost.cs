using System.Collections.Concurrent;
using Terminal.Gui.App;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Operations;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;

namespace Vega.TerminalUI;

public sealed class TerminalUiHost : IDisposable
{
    private const string VegaSchemeName = "VegaDark";
    private readonly OperationsDashboardViewModel viewModel;
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
        string? layoutPath = null)
    {
        viewModel = new OperationsDashboardViewModel(snapshots, commands, unhandledCommands);
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
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(4),
                SchemeName = VegaSchemeName,
                Visible = false
            };
            dashboard = new OperationsDashboardView(desktop, layout);

            var commandBar = new FrameView
            {
                Title = "Command palette",
                X = 0,
                Y = Pos.AnchorEnd(4),
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
            var result = new Label
            {
                X = Pos.Right(command) + 2,
                Y = 1,
                Width = Dim.Fill(1),
                Text = "Ready. Drag window titles; drag borders to resize."
            };
            var results = new ConcurrentQueue<string>();

            command.Accepting += (_, eventArgs) =>
            {
                string input = command.Text;
                command.Text = string.Empty;
                eventArgs.Handled = true;
                _ = ExecuteAsync(input, results, dashboard);
            };

            commandBar.Add(prompt, command, result);
            window.Add(startup, desktop, commandBar);
            bool dashboardVisible = false;
            app.AddTimeout(TimeSpan.FromMilliseconds(layout.RefreshIntervalMilliseconds), () =>
            {
                if (shutdown.IsCancellationRequested)
                {
                    app.RequestStop(window);
                    return false;
                }

                OperationsSnapshot snapshot = viewModel.Refresh();
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
                        desktop.Visible = true;
                        commandBar.Visible = true;
                        command.SetFocus();
                    }

                    dashboard.Update(snapshot, viewModel.IsFollowing, viewModel.LogFilter);
                }

                while (results.TryDequeue(out string? message))
                {
                    result.Text = message;
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
