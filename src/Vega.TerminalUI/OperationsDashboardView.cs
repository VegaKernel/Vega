using System.Drawing;
using System.Globalization;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Operations;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;
using TuiColor = Terminal.Gui.Drawing.Color;

namespace Vega.TerminalUI;

internal sealed class OperationsDashboardView
{
    private const string SchemeName = "VegaDark";
    private readonly View desktop;
    private readonly Dictionary<string, View> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label overviewText = CreateLabel();
    private readonly Label networkText = CreateLabel();
    private readonly Label playersText = CreateLabel();
    private readonly Label modulesText = CreateLabel();
    private readonly Editor logsText = CreateLogEditor();
    private readonly GraphView performanceGraph = CreateGraph("TPS + CPU");
    private readonly GraphView memoryGraph = CreateGraph("Memory");
    private readonly PathAnnotation tpsPath = new() { LineColor = new TuiAttribute(TuiColor.BrightGreen, TuiColor.Black) };
    private readonly PathAnnotation cpuPath = new() { LineColor = new TuiAttribute(TuiColor.BrightCyan, TuiColor.Black) };
    private readonly PathAnnotation workingSetPath = new() { LineColor = new TuiAttribute(TuiColor.BrightYellow, TuiColor.Black) };
    private readonly PathAnnotation gcHeapPath = new() { LineColor = new TuiAttribute(TuiColor.BrightMagenta, TuiColor.Black) };
    private readonly OperationsMetricHistory history;
    private TerminalUiLayoutOptions layout;

    public OperationsDashboardView(View desktop, TerminalUiLayoutOptions layout)
    {
        this.desktop = desktop;
        this.layout = TerminalUiLayoutOptions.Normalize(layout);
        history = new OperationsMetricHistory(this.layout.HistorySamples);

        AddTextWindow(TerminalUiLayoutOptions.Overview, "Server / world", overviewText);
        AddGraphWindow(TerminalUiLayoutOptions.Performance, performanceGraph);
        AddTextWindow(TerminalUiLayoutOptions.Players, "Players", playersText);
        AddGraphWindow(TerminalUiLayoutOptions.Memory, memoryGraph);
        AddTextWindow(TerminalUiLayoutOptions.Network, "Network", networkText);
        AddTextWindow(TerminalUiLayoutOptions.Modules, "Modules", modulesText);
        AddTextWindow(TerminalUiLayoutOptions.Logs, "Logs", logsText);

        ConfigurePerformanceGraph();
        ConfigureMemoryGraph();
    }

    public void Update(OperationsSnapshot snapshot, bool isFollowing, OperationsLogFilter filter)
    {
        OperationsMetricSample sample = history.Add(snapshot);
        SetText(overviewText, RenderOverview(snapshot));
        SetText(networkText, RenderNetwork(snapshot, sample));
        SetText(playersText, RenderPlayers(snapshot));
        SetText(modulesText, RenderModules(snapshot));
        SetLogText(RenderLogs(snapshot, isFollowing, filter));
        UpdateGraphs(sample);
    }

    public bool TryExecuteLayoutCommand(string input, out string message, out bool saveRequested)
    {
        saveRequested = false;
        string[] parts = input.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        string action = parts.ElementAtOrDefault(0)?.ToLowerInvariant() ?? string.Empty;
        string name = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? string.Empty;

        if (action == ":windows")
        {
            message = string.Join(
                " · ",
                TerminalUiLayoutOptions.WindowNames.Select(windowName =>
                    $"{windowName}={(windows[windowName].Visible ? "on" : "off")}"));
            return true;
        }

        if (action is ":show" or ":hide")
        {
            if (!windows.TryGetValue(name, out View? view))
            {
                message = $"Unknown window '{name}'. Use :windows.";
                return true;
            }

            view.Visible = action == ":show";
            if (view.Visible)
            {
                view.SetFocus();
            }

            message = $"Window {name}: {(view.Visible ? "visible" : "hidden")}.";
            return true;
        }

        if (action == ":layout" && name == "save")
        {
            saveRequested = true;
            message = "Window layout saved.";
            return true;
        }

        if (action == ":layout" && name == "reset")
        {
            ApplyLayout(TerminalUiLayoutOptions.Default);
            saveRequested = true;
            message = "Window layout reset and saved.";
            return true;
        }

        message = string.Empty;
        return false;
    }

    public TerminalUiLayoutOptions CaptureLayout()
    {
        if (desktop.Viewport.Width <= 1 || desktop.Viewport.Height <= 1)
        {
            return layout;
        }

        int desktopWidth = Math.Max(1, desktop.Viewport.Width);
        int desktopHeight = Math.Max(1, desktop.Viewport.Height);
        var captured = new Dictionary<string, TerminalUiPanelLayout>(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, View view) in windows)
        {
            Rectangle frame = view.Frame;
            captured[name] = new TerminalUiPanelLayout(
                view.Visible,
                ToPercent(frame.X, desktopWidth),
                ToPercent(frame.Y, desktopHeight),
                ToPercent(frame.Width, desktopWidth),
                ToPercent(frame.Height, desktopHeight));
        }

        return TerminalUiLayoutOptions.Normalize(layout with { Windows = captured });
    }

    private void ApplyLayout(TerminalUiLayoutOptions value)
    {
        layout = TerminalUiLayoutOptions.Normalize(value);
        foreach ((string name, View view) in windows)
        {
            Apply(view, layout.GetWindow(name));
        }
    }

    private void AddTextWindow(string name, string title, View text)
    {
        var frame = new FrameView
        {
            Title = title,
            BorderStyle = LineStyle.Single,
            SchemeName = SchemeName,
            Arrangement = ViewArrangement.Movable | ViewArrangement.Resizable | ViewArrangement.Overlapped
        };
        text.X = 0;
        text.Y = 0;
        text.Width = Dim.Fill();
        text.Height = Dim.Fill();
        frame.Add(text);
        windows.Add(name, frame);
        Apply(frame, layout.GetWindow(name));
        desktop.Add(frame);
    }

    private void AddGraphWindow(string name, GraphView graph)
    {
        windows.Add(name, graph);
        Apply(graph, layout.GetWindow(name));
        desktop.Add(graph);
    }

    private static void Apply(View view, TerminalUiPanelLayout panel)
    {
        view.X = Pos.Percent(panel.XPercent);
        view.Y = Pos.Percent(panel.YPercent);
        view.Width = Dim.Percent(panel.WidthPercent);
        view.Height = Dim.Percent(panel.HeightPercent);
        view.Visible = panel.Visible;
    }

    private void ConfigurePerformanceGraph()
    {
        performanceGraph.Annotations.Add(tpsPath);
        performanceGraph.Annotations.Add(cpuPath);
        performanceGraph.AxisX.Visible = false;
        performanceGraph.AxisY.Minimum = 0;
        performanceGraph.AxisY.Increment = 20;
        performanceGraph.AxisY.ShowLabelsEvery = 1;
        performanceGraph.AxisY.LabelGetter = value => value.Value.ToString("N0", CultureInfo.InvariantCulture);
        performanceGraph.MarginLeft = 4;
        performanceGraph.MarginBottom = 0;
    }

    private void ConfigureMemoryGraph()
    {
        memoryGraph.Annotations.Add(workingSetPath);
        memoryGraph.Annotations.Add(gcHeapPath);
        memoryGraph.AxisX.Visible = false;
        memoryGraph.AxisY.Minimum = 0;
        memoryGraph.AxisY.ShowLabelsEvery = 1;
        memoryGraph.AxisY.LabelGetter = value => value.Value.ToString("N0", CultureInfo.InvariantCulture);
        memoryGraph.MarginLeft = 5;
        memoryGraph.MarginBottom = 0;
    }

    private void UpdateGraphs(OperationsMetricSample current)
    {
        IReadOnlyList<OperationsMetricSample> samples = history.Samples;
        tpsPath.Points = Points(samples, static sample => (float)sample.TicksPerSecond);
        cpuPath.Points = Points(samples, static sample => (float)sample.CpuPercent);
        performanceGraph.Title = $"TPS green {current.TicksPerSecond:F1} · CPU cyan {current.CpuPercent:F1}%";
        FitGraph(performanceGraph, samples.Count, 100);

        workingSetPath.Points = Points(samples, static sample => (float)sample.WorkingSetMiB);
        gcHeapPath.Points = Points(samples, static sample => (float)sample.GcHeapMiB);
        float memoryMaximum = (float)Math.Max(
            64,
            samples.Max(static sample => Math.Max(sample.WorkingSetMiB, sample.GcHeapMiB)) * 1.15);
        memoryGraph.AxisY.Increment = NiceIncrement(memoryMaximum / 3);
        memoryGraph.Title = $"Working yellow {current.WorkingSetMiB:F0} MiB · GC magenta {current.GcHeapMiB:F0} MiB";
        FitGraph(memoryGraph, samples.Count, memoryMaximum);
    }

    private static void FitGraph(GraphView graph, int sampleCount, float yMaximum)
    {
        int width = Math.Max(1, graph.Viewport.Width - (int)graph.MarginLeft);
        int height = Math.Max(1, graph.Viewport.Height - (int)graph.MarginBottom);
        graph.CellSize = new PointF(
            Math.Max(1, (sampleCount - 1f) / width),
            Math.Max(0.1f, yMaximum / height));
        graph.ScrollOffset = PointF.Empty;
        graph.SetNeedsDraw();
    }

    private static List<PointF> Points(
        IReadOnlyList<OperationsMetricSample> samples,
        Func<OperationsMetricSample, float> selector) =>
        samples.Select((sample, index) => new PointF(index, selector(sample))).ToList();

    private static float NiceIncrement(float value)
    {
        if (value <= 1)
        {
            return 1;
        }

        double power = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double normalized = value / power;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return (float)(nice * power);
    }

    private static string RenderOverview(OperationsSnapshot snapshot)
    {
        ServerDashboardSnapshot server = snapshot.Server;
        var text = new StringBuilder()
            .Append(server.ServerName).Append(' ').Append(server.Version)
            .Append("  ·  ").Append(server.LifecycleStage).AppendLine()
            .Append("World: ").Append(server.WorldName ?? "not loaded")
            .Append("  ·  port ").Append(server.ListenerPort).AppendLine()
            .Append("Players: ").Append(server.OnlinePlayers).Append('/').Append(server.MaximumPlayers)
            .Append("  ·  uptime ").Append(FormatDuration(server.Uptime));

        if (snapshot.WorldLoad is { } worldLoad)
        {
            text.AppendLine()
                .Append("World image: ").Append(worldLoad.CacheHit ? "cache hit" : worldLoad.CacheReason)
                .Append("  ·  ready ").Append(worldLoad.WorldReadyDuration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append('s');
        }

        if (server.Warnings.Count > 0)
        {
            text.AppendLine().Append("⚠ ").Append(string.Join(" | ", server.Warnings));
        }

        return text.ToString();
    }

    private static string RenderNetwork(OperationsSnapshot snapshot, OperationsMetricSample sample) =>
        $"IN   {sample.InboundKiBPerSecond,8:F1} KiB/s   {snapshot.Network.InboundPackets,12:N0} packets\n" +
        $"OUT  {sample.OutboundKiBPerSecond,8:F1} KiB/s   {snapshot.Network.OutboundPackets,12:N0} packets\n\n" +
        $"Totals: ↓ {FormatBytes(snapshot.Network.InboundBytes)}  ↑ {FormatBytes(snapshot.Network.OutboundBytes)}\n" +
        "Packet/security breakdown appears here when Phase 13 counters exist.";

    private static string RenderPlayers(OperationsSnapshot snapshot)
    {
        if (snapshot.Players.Count == 0)
        {
            return "No connected players.";
        }

        return string.Join(
            Environment.NewLine,
            snapshot.Players.Select(player =>
                $"[{player.Id.Slot,3}] {player.Name,-18} HP {player.Life,4}/{player.MaxLife,-4} MP {player.Mana,3}/{player.MaxMana,-3} T{player.Team} {(player.IsHostile ? "PvP" : string.Empty)}"));
    }

    private static string RenderModules(OperationsSnapshot snapshot) => string.Join(
        Environment.NewLine,
        snapshot.Modules.Select(module =>
            $"{StateGlyph(module.State)} {module.Name} {module.Version}\n   {module.StartupDuration.TotalMilliseconds:F1} ms{FormatFailure(module.Failure)}"));

    private static string RenderLogs(
        OperationsSnapshot snapshot,
        bool isFollowing,
        OperationsLogFilter filter)
    {
        string header = $"{(isFollowing ? "FOLLOW" : "PAUSED")} · level {filter.MinimumLevel?.ToString() ?? "all"} · source {filter.Source ?? "all"} · text {filter.Text ?? "all"}";
        return header + Environment.NewLine + string.Join(
            Environment.NewLine,
            snapshot.Logs.Select(record =>
                $"{record.Timestamp:HH:mm:ss} {LevelGlyph(record.Level)} {record.Source,-14} {record.Text}"));
    }

    private static Label CreateLabel() => new()
    {
        SchemeName = SchemeName
    };

    private static Editor CreateLogEditor() => new()
    {
        ReadOnly = true,
        WordWrap = false,
        GutterOptions = GutterOptions.None,
        ViewportSettings = ViewportSettingsFlags.HasScrollBars,
        Document = new TextDocument(string.Empty),
        SchemeName = SchemeName
    };

    private static GraphView CreateGraph(string title) => new()
    {
        Title = title,
        BorderStyle = LineStyle.Single,
        SchemeName = SchemeName,
        Arrangement = ViewArrangement.Movable | ViewArrangement.Resizable | ViewArrangement.Overlapped,
        GraphColor = new TuiAttribute(TuiColor.White, TuiColor.Black)
    };

    private static void SetText(Label view, string text)
    {
        if (!string.Equals(view.Text, text, StringComparison.Ordinal))
        {
            view.Text = text;
        }
    }

    private string lastLogText = string.Empty;

    private void SetLogText(string text)
    {
        if (string.Equals(lastLogText, text, StringComparison.Ordinal))
        {
            return;
        }

        lastLogText = text;
        logsText.Document = new TextDocument(text);
    }

    private static int ToPercent(int value, int total) =>
        Math.Clamp((int)Math.Round(value * 100d / total), 0, 100);

    private static string StateGlyph(ModuleLoadState state) => state switch
    {
        ModuleLoadState.Loaded => "●",
        ModuleLoadState.Registered => "○",
        ModuleLoadState.Degraded => "▲",
        ModuleLoadState.Failed => "✕",
        _ => "−"
    };

    private static string LevelGlyph(OperationsLogLevel level) => level switch
    {
        OperationsLogLevel.Error or OperationsLogLevel.Critical => "ERR",
        OperationsLogLevel.Warning => "WRN",
        OperationsLogLevel.Debug or OperationsLogLevel.Trace => "DBG",
        _ => "INF"
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:F1} {units[unit]}");
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalDays >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalDays:F1}d")
            : duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

    private static string FormatFailure(string? failure) =>
        string.IsNullOrWhiteSpace(failure) ? string.Empty : $" · {failure}";
}
