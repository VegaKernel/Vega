using System.Collections.Concurrent;
using Terminal.Gui.App;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using TShockNG.Contracts.Operations;

namespace Vega.TerminalUI;

public sealed class TerminalUiHost : IDisposable
{
    private readonly OperationsDashboardViewModel viewModel;
    private readonly IOperationsLogger logger;
    private readonly CancellationTokenSource shutdown = new();
    private Thread? uiThread;

    public TerminalUiHost(
        IOperationsSnapshotProvider snapshots,
        IOperationsCommandService commands,
        IOperationsLogger logger)
    {
        viewModel = new OperationsDashboardViewModel(snapshots, commands);
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

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
            return true;
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

        shutdown.Dispose();
    }

    private void Run()
    {
        try
        {
            using IApplication app = Application.Create();
            app.Init();
            using Window window = new() { Title = "Vega Operations — Esc quits UI only" };
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
                Document = new TextDocument(viewModel.Render())
            };
            var prompt = new Label
            {
                X = 0,
                Y = Pos.Bottom(content),
                Text = "Command (:pause/:follow/:level/:source/:find/:clear):"
            };
            var command = new TextField
            {
                X = 0,
                Y = Pos.Bottom(prompt),
                Width = Dim.Fill(),
                Text = string.Empty
            };
            var result = new Label
            {
                X = 0,
                Y = Pos.Bottom(command),
                Width = Dim.Fill(),
                Text = "Ready."
            };
            var results = new ConcurrentQueue<string>();

            command.Accepting += (_, eventArgs) =>
            {
                string input = command.Text;
                command.Text = string.Empty;
                eventArgs.Handled = true;
                _ = ExecuteAsync(input, results);
            };

            window.Add(content, prompt, command, result);
            app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
            {
                if (shutdown.IsCancellationRequested)
                {
                    app.RequestStop(window);
                    return false;
                }

                content.Document = new TextDocument(viewModel.Render());
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
}
