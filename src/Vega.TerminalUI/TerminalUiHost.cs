using System.Collections.Concurrent;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

public sealed class TerminalUiHost : IDisposable
{
    private readonly OperationsDashboardViewModel viewModel;
    private readonly IOperationsLogger logger;
    private readonly IStartupProgressReader startupProgress;
    private readonly CancellationTokenSource shutdown = new();
    private readonly ManualResetEventSlim initialized = new(false);
    private Thread? uiThread;
    private int running;

    public TerminalUiHost(
        IOperationsSnapshotProvider snapshots,
        IOperationsCommandService commands,
        IOperationsLogger logger,
        IStartupProgressReader? startupProgress = null,
        IUnhandledOperationsCommandSink? unhandledCommands = null)
    {
        viewModel = new OperationsDashboardViewModel(snapshots, commands, unhandledCommands);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.startupProgress = startupProgress ?? WaitingStartupProgressReader.Instance;
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
        try
        {
            using IApplication app = Application.Create();
            if (OperatingSystem.IsWindows())
            {
                app.ForceDriver = DriverRegistry.Names.WINDOWS;
            }

            app.Init();
            Volatile.Write(ref running, 1);
            Started?.Invoke(Environment.CurrentManagedThreadId);
            initialized.Set();
            using Window window = new() { Title = "Vega Operations" };
            var startup = new FrameView
            {
                Title = "Starting server",
                X = 1,
                Y = 1,
                Width = Dim.Fill(1),
                Height = 9
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

            var content = new Editor
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(3),
                ReadOnly = true,
                WordWrap = false,
                GutterOptions = GutterOptions.None,
                ViewportSettings = ViewportSettingsFlags.HasScrollBars,
                Document = new TextDocument(string.Empty),
                Visible = false
            };
            var prompt = new Label
            {
                X = 0,
                Y = Pos.Bottom(content),
                Text = "Command (:pause/:follow/:level/:source/:find/:clear):",
                Visible = false
            };
            var command = new TextField
            {
                X = 0,
                Y = Pos.Bottom(prompt),
                Width = Dim.Fill(),
                Text = string.Empty,
                Visible = false
            };
            var result = new Label
            {
                X = 0,
                Y = Pos.Bottom(command),
                Width = Dim.Fill(),
                Text = "Ready.",
                Visible = false
            };
            var results = new ConcurrentQueue<string>();

            command.Accepting += (_, eventArgs) =>
            {
                string input = command.Text;
                command.Text = string.Empty;
                eventArgs.Handled = true;
                _ = ExecuteAsync(input, results);
            };

            window.Add(startup, content, prompt, command, result);
            bool dashboardVisible = false;
            app.AddTimeout(TimeSpan.FromMilliseconds(150), () =>
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
                        content.Visible = true;
                        prompt.Visible = true;
                        command.Visible = true;
                        result.Visible = true;
                        command.SetFocus();
                    }

                    content.Document = new TextDocument(viewModel.Render());
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
            Volatile.Write(ref running, 0);
            initialized.Set();
            Stopped?.Invoke(!shutdown.IsCancellationRequested);
        }
    }

    private async Task ExecuteAsync(string input, ConcurrentQueue<string> results)
    {
        try
        {
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
