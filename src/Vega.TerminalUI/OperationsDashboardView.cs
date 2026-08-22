using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using System.Text;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Bans;
using Vega.Contracts.Moderation;
using Vega.Contracts.Worlds;
using TuiAttribute = Terminal.Gui.Drawing.Attribute;
using TuiColor = Terminal.Gui.Drawing.Color;

namespace Vega.TerminalUI;

internal sealed class OperationsDashboardView : IDisposable
{
    private const string SchemeName = "VegaDark";
    private const double TargetTicksPerSecond = 60d;
    private readonly View desktop;
    private readonly Dictionary<string, View> windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TerminalUiPanelLayout> restoreLayouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WindowInteraction> windowInteractions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label overviewText = CreateLabel();
    private readonly Label networkText = CreateLabel();
    private readonly ListView playersList = new();
    private readonly ListView modulesList = new();
    private readonly ListView regionsList = new();
    private readonly ListView moderationList = new();
    private readonly Editor logsText = CreateLogEditor();
    private readonly Editor chatText = CreateLogEditor();
    private readonly GraphView performanceGraph = CreateGraph("TPS / CPU");
    private readonly GraphView memoryGraph = CreateGraph("Memory");
    private readonly PathAnnotation tpsTargetPath = new() { LineColor = new TuiAttribute(TuiColor.Gray, TuiColor.Black) };
    private readonly PathAnnotation tpsPath = new() { LineColor = new TuiAttribute(TuiColor.BrightGreen, TuiColor.Black) };
    private readonly PathAnnotation cpuPath = new() { LineColor = new TuiAttribute(TuiColor.BrightCyan, TuiColor.Black) };
    private readonly PathAnnotation workingSetPath = new() { LineColor = new TuiAttribute(TuiColor.BrightYellow, TuiColor.Black) };
    private readonly PathAnnotation gcHeapPath = new() { LineColor = new TuiAttribute(TuiColor.BrightMagenta, TuiColor.Black) };
    private readonly OperationsMetricHistory history;
    private IReadOnlyList<PlayerListRow> playerListRows = Array.Empty<PlayerListRow>();
    private IReadOnlyList<ModuleOperationsSnapshot> modules = Array.Empty<ModuleOperationsSnapshot>();
    private string[] playerRows = Array.Empty<string>();
    private string[] moduleRows = Array.Empty<string>();
    private string[] regionRows = Array.Empty<string>();
    private string[] moderationRows = Array.Empty<string>();
    private PopoverMenu? playerMenu;
    private PopoverMenu? moduleMenu;
    private PlayerId? draggedPlayerId;
    private bool canMovePlayersBetweenWorlds;
    private string lastLogText = string.Empty;
    private string pendingLogText = string.Empty;
    private string lastChatText = string.Empty;
    private string pendingChatText = string.Empty;
    private TerminalUiLayoutOptions layout;

    public OperationsDashboardView(View desktop, TerminalUiLayoutOptions layout)
    {
        this.desktop = desktop;
        this.layout = TerminalUiLayoutOptions.Normalize(layout);
        history = new OperationsMetricHistory(this.layout.HistorySamples);

        playersList.SchemeName = SchemeName;
        playersList.ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar;
        modulesList.SchemeName = SchemeName;
        modulesList.ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar;
        regionsList.SchemeName = SchemeName;
        regionsList.ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar;
        moderationList.SchemeName = SchemeName;
        moderationList.ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar;

        AddTextWindow(TerminalUiLayoutOptions.Overview, "Server", overviewText);
        AddGraphWindow(TerminalUiLayoutOptions.Performance, performanceGraph);
        AddListWindow(TerminalUiLayoutOptions.Players, "Players / Worlds", playersList, ConfigurePlayerContextMenu);
        AddGraphWindow(TerminalUiLayoutOptions.Memory, memoryGraph);
        AddTextWindow(TerminalUiLayoutOptions.Network, "Network", networkText);
        AddListWindow(TerminalUiLayoutOptions.Modules, "Modules", modulesList, ConfigureModuleContextMenu);
        AddListWindow(TerminalUiLayoutOptions.Regions, "Regions", regionsList, static () => { });
        AddListWindow(TerminalUiLayoutOptions.Moderation, "Moderation", moderationList, static () => { });
        AddLogsWindow();
        AddTextWindow(TerminalUiLayoutOptions.Chat, "Chat", chatText);

        ConfigurePlayerDragAndDrop();
        ConfigurePerformanceGraph();
        ConfigureMemoryGraph();
    }

    public event Action? LogFilterRequested;

    public event Action? LogFollowToggleRequested;

    public event Action? LogFiltersClearRequested;

    public event Action<string>? CommandRequested;

    public event Action<PlayerId, WorldId>? PlayerMoveRequested;

    public void Update(OperationsSnapshot snapshot, bool isFollowing, OperationsLogFilter filter)
    {
        OperationsMetricSample sample = history.Add(snapshot);
        modules = snapshot.Modules;
        canMovePlayersBetweenWorlds = snapshot.CanMovePlayersBetweenWorlds;
        SetText(overviewText, RenderOverview(snapshot));
        SetText(networkText, RenderNetwork(snapshot, sample));
        UpdatePlayerList(snapshot);
        UpdateModuleList(snapshot);
        UpdateRegionList(snapshot);
        UpdateModerationList(snapshot);
        SetLogText(RenderLogs(snapshot, filter), logsText, ref lastLogText, ref pendingLogText);
        SetLogText(RenderChat(snapshot), chatText, ref lastChatText, ref pendingChatText);
        if (windows[TerminalUiLayoutOptions.Logs] is FrameView logsFrame)
        {
            logsFrame.Title = RenderLogTitle(isFollowing, filter);
        }

        UpdateGraphs(sample);
    }

    public bool ToggleWindowVisibility(string name)
    {
        if (!windows.TryGetValue(name, out View? view))
        {
            return false;
        }

        view.Visible = !view.Visible;
        if (view.Visible)
        {
            view.SetFocus();
        }

        return true;
    }

    public bool FocusWindow(string name)
    {
        if (!windows.TryGetValue(name, out View? view))
        {
            return false;
        }

        view.Visible = true;
        view.SetFocus();
        return true;
    }

    public void ResetLayout()
    {
        restoreLayouts.Clear();
        ApplyLayout(TerminalUiLayoutOptions.Default);
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
            ResetLayout();
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
            if (restoreLayouts.TryGetValue(name, out TerminalUiPanelLayout? restore))
            {
                captured[name] = restore with { Visible = view.Visible };
                continue;
            }

            captured[name] = CapturePanel(view, desktopWidth, desktopHeight);
        }

        return TerminalUiLayoutOptions.Normalize(layout with { Windows = captured });
    }

    public void Dispose()
    {
        DisposePopover(playersList, ref playerMenu);
        DisposePopover(modulesList, ref moduleMenu);
    }

    private void AddTextWindow(string name, string title, View text)
    {
        FrameView frame = CreateFrame(name, title);
        text.X = 0;
        text.Y = 0;
        text.Width = Dim.Fill();
        text.Height = Dim.Fill();
        frame.Add(text);
        RegisterWindow(name, frame);
    }

    private void AddListWindow(string name, string title, ListView list, Action configureContextMenu)
    {
        FrameView frame = CreateFrame(name, title);
        list.X = 0;
        list.Y = 0;
        list.Width = Dim.Fill();
        list.Height = Dim.Fill();
        frame.Add(list);
        RegisterWindow(name, frame);
        list.Initialized += (_, _) => configureContextMenu();
    }

    private void AddLogsWindow()
    {
        FrameView frame = CreateFrame(TerminalUiLayoutOptions.Logs, "Logs");
        var localMenu = new MenuBar([
            new MenuBarItem("_Logs", [
                new MenuItem("_Filter...", action: () => LogFilterRequested?.Invoke()),
                new MenuItem("Follow / _pause", action: () => LogFollowToggleRequested?.Invoke()),
                new MenuItem("_Clear filters", action: () => LogFiltersClearRequested?.Invoke())
            ])
        ])
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            SchemeName = SchemeName
        };
        logsText.X = 0;
        logsText.Y = 1;
        logsText.Width = Dim.Fill();
        logsText.Height = Dim.Fill();
        frame.Add(localMenu, logsText);
        RegisterWindow(TerminalUiLayoutOptions.Logs, frame);
    }

    private void AddGraphWindow(string name, GraphView graph)
    {
        AttachDoubleClickMaximize(name, graph);
        RegisterWindow(name, graph);
    }

    private FrameView CreateFrame(string name, string title)
    {
        var frame = new FrameView
        {
            Title = title,
            BorderStyle = LineStyle.Single,
            SchemeName = SchemeName,
            Arrangement = ViewArrangement.Movable | ViewArrangement.Resizable | ViewArrangement.Overlapped
        };
        AttachDoubleClickMaximize(name, frame);
        return frame;
    }

    private void RegisterWindow(string name, View view)
    {
        windows.Add(name, view);
        Apply(view, layout.GetWindow(name));
        AttachTiling(name, view);
        desktop.Add(view);
    }

    private void AttachDoubleClickMaximize(string name, View view)
    {
        view.Initialized += (_, _) =>
        {
            if (view.Border.View is not View borderView)
            {
                return;
            }

            borderView.MouseBindings.Add(MouseFlags.LeftButtonDoubleClicked, Command.Accept);
            borderView.Accepting += (_, args) =>
            {
                if (args.Context?.Binding is MouseBinding { MouseEvent: { Position: { } position } } &&
                    position.Y == 0)
                {
                    ToggleMaximize(name);
                    args.Handled = true;
                }
            };
        };
    }

    private void AttachTiling(string name, View view)
    {
        view.MouseEvent += (_, args) =>
        {
            if (restoreLayouts.ContainsKey(name))
            {
                return;
            }

            if (args.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                windowInteractions[name] = new WindowInteraction(
                    view.Frame,
                    args.Position?.Y == 0);
                return;
            }

            if (!args.Flags.HasFlag(MouseFlags.LeftButtonReleased) ||
                !windowInteractions.Remove(name, out WindowInteraction? interaction) ||
                interaction is null)
            {
                return;
            }

            CompleteTilingInteraction(name, view, interaction);
        };
    }

    private void CompleteTilingInteraction(string name, View view, WindowInteraction interaction)
    {
        if (desktop.Viewport.Width <= 1 || desktop.Viewport.Height <= 1)
        {
            return;
        }

        Rectangle changed = view.Frame;
        if (changed == interaction.Frame)
        {
            return;
        }

        Dictionary<string, Rectangle> frames = windows
            .Where(static pair => pair.Value.Visible)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value.Frame, StringComparer.OrdinalIgnoreCase);
        frames[name] = interaction.Frame;

        if (interaction.StartedOnTitle)
        {
            string? targetName = TerminalUiTiling.FindSwapTarget(name, changed, frames);
            if (targetName is not null)
            {
                Rectangle targetFrame = frames[targetName];
                ApplyFrame(windows[targetName], interaction.Frame);
                ApplyFrame(view, targetFrame);
                return;
            }

            ApplyFrame(view, interaction.Frame);
            return;
        }

        if (TerminalUiTiling.TryResizeSharedEdges(
                name,
                interaction.Frame,
                changed,
                frames,
                new Size(desktop.Viewport.Width, desktop.Viewport.Height),
                out IReadOnlyDictionary<string, Rectangle> resized))
        {
            foreach ((string windowName, Rectangle frame) in resized)
            {
                ApplyFrame(windows[windowName], frame);
            }

            return;
        }

        ApplyFrame(view, interaction.Frame);
    }

    private void ApplyFrame(View view, Rectangle frame)
    {
        view.X = Pos.Percent(ToPercent(frame.X, desktop.Viewport.Width));
        view.Y = Pos.Percent(ToPercent(frame.Y, desktop.Viewport.Height));
        view.Width = Dim.Percent(ToPercent(frame.Width, desktop.Viewport.Width));
        view.Height = Dim.Percent(ToPercent(frame.Height, desktop.Viewport.Height));
    }

    private void ToggleMaximize(string name)
    {
        if (!windows.TryGetValue(name, out View? view) || desktop.Viewport.Width <= 1 || desktop.Viewport.Height <= 1)
        {
            return;
        }

        if (restoreLayouts.Remove(name, out TerminalUiPanelLayout? restore))
        {
            Apply(view, restore with { Visible = true });
            return;
        }

        restoreLayouts[name] = CapturePanel(view, desktop.Viewport.Width, desktop.Viewport.Height);
        view.X = 0;
        view.Y = 0;
        view.Width = Dim.Fill();
        view.Height = Dim.Fill();
        view.Visible = true;
        view.SetFocus();
    }

    private void ConfigurePlayerContextMenu()
    {
        playersList.MouseBindings.Add(MouseFlags.RightButtonClicked, Command.Context);
        playersList.CommandNotBound += (_, args) =>
        {
            if (args.Context?.Binding is not MouseBinding { MouseEvent: { } mouse })
            {
                return;
            }

            int index = mouse.Position?.Y + playersList.Viewport.Y ?? -1;
            if ((uint)index >= (uint)playerListRows.Count || playerListRows[index].Player is not PlayerSnapshot player)
            {
                return;
            }

            playersList.SelectedItem = index;
            DisposePopover(playersList, ref playerMenu);
            playerMenu = new PopoverMenu([
                new MenuItem("_Kick", action: () => CommandRequested?.Invoke($"kick tsi:{player.Id.Slot} Operator action")),
                new MenuItem("_Ban", action: () => CommandRequested?.Invoke($"ban tsi:{player.Id.Slot} Operator action")),
                new Line(),
                new MenuItem("_Details", action: () => ShowPlayerDetails(player))
            ]);
            playersList.App?.Popovers?.Register(playerMenu);
            playerMenu.MakeVisible(mouse.ScreenPosition);
            args.Handled = true;
        };
    }

    private void ConfigurePlayerDragAndDrop()
    {
        playersList.MouseEvent += (_, args) =>
        {
            int index = args.Position?.Y + playersList.Viewport.Y ?? -1;
            if (args.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                draggedPlayerId = (uint)index < (uint)playerListRows.Count
                    ? playerListRows[index].Player?.Id
                    : null;
                return;
            }

            if (!args.Flags.HasFlag(MouseFlags.LeftButtonReleased))
            {
                return;
            }

            PlayerId? dragged = draggedPlayerId;
            draggedPlayerId = null;
            if (dragged is null || !canMovePlayersBetweenWorlds || (uint)index >= (uint)playerListRows.Count)
            {
                return;
            }

            PlayerListRow targetRow = playerListRows[index];
            WorldId? targetWorldId = targetRow.Context?.Id ?? targetRow.Player?.WorldId;
            PlayerSnapshot? sourcePlayer = playerListRows
                .Select(static row => row.Player)
                .FirstOrDefault(player => player?.Id == dragged.Value);
            if (sourcePlayer is null || targetWorldId is null || sourcePlayer.WorldId == targetWorldId.Value)
            {
                return;
            }

            PlayerMoveRequested?.Invoke(dragged.Value, targetWorldId.Value);
            args.Handled = true;
        };
    }

    private void ConfigureModuleContextMenu()
    {
        modulesList.MouseBindings.Add(MouseFlags.RightButtonClicked, Command.Context);
        modulesList.CommandNotBound += (_, args) =>
        {
            if (args.Context?.Binding is not MouseBinding { MouseEvent: { } mouse })
            {
                return;
            }

            int index = mouse.Position?.Y + modulesList.Viewport.Y ?? -1;
            if ((uint)index >= (uint)modules.Count)
            {
                return;
            }

            modulesList.SelectedItem = index;
            ModuleOperationsSnapshot module = modules[index];
            DisposePopover(modulesList, ref moduleMenu);
            var reload = new MenuItem("_Reload", action: () => CommandRequested?.Invoke($"plugin reload {QuoteArgument(module.Name)}"))
            {
                Enabled = module.IsHotReloadable,
                HelpText = module.IsHotReloadable ? string.Empty : "Built-in modules are not hot-reloadable."
            };
            var unload = new MenuItem("_Unload", action: () => CommandRequested?.Invoke($"plugin unload {QuoteArgument(module.Name)}"))
            {
                Enabled = module.IsHotReloadable,
                HelpText = module.IsHotReloadable ? string.Empty : "Built-in modules are not hot-reloadable."
            };
            moduleMenu = new PopoverMenu([reload, unload]);
            modulesList.App?.Popovers?.Register(moduleMenu);
            moduleMenu.MakeVisible(mouse.ScreenPosition);
            args.Handled = true;
        };
    }

    private void ShowPlayerDetails(PlayerSnapshot player)
    {
        string connected = player.ConnectedAt is DateTimeOffset connectedAt
            ? FormatDuration(DateTimeOffset.UtcNow - connectedAt)
            : "unknown";
        MessageBox.Query(
            playersList.App!,
            player.Name,
            $"Slot: {player.Id.Slot}\nWorld: {player.WorldId}\nIP/endpoint: {player.RemoteEndpoint ?? "unknown"}\nPlatform: {player.Platform.Kind}\nSession: {connected}\nHP: {player.Life}/{player.MaxLife}\nMP: {player.Mana}/{player.MaxMana}",
            "OK");
    }

    private void ApplyLayout(TerminalUiLayoutOptions value)
    {
        layout = TerminalUiLayoutOptions.Normalize(value);
        foreach ((string name, View view) in windows)
        {
            Apply(view, layout.GetWindow(name));
        }
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
        performanceGraph.Annotations.Add(tpsTargetPath);
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
        tpsTargetPath.Points = samples.Select((_, index) => new PointF(index, (float)TargetTicksPerSecond)).ToList();
        tpsPath.Points = Points(samples, static sample => (float)sample.TicksPerSecond);
        cpuPath.Points = Points(samples, static sample => (float)sample.CpuPercent);
        performanceGraph.Title = $"TPS {current.TicksPerSecond:F1}/{TargetTicksPerSecond:F0} · CPU {current.CpuPercent:F1}%";
        FitGraph(performanceGraph, samples.Count, 100);

        workingSetPath.Points = Points(samples, static sample => (float)sample.WorkingSetMiB);
        gcHeapPath.Points = Points(samples, static sample => (float)sample.GcHeapMiB);
        float memoryMaximum = (float)Math.Max(
            64,
            samples.Max(static sample => Math.Max(sample.WorkingSetMiB, sample.GcHeapMiB)) * 1.15);
        memoryGraph.AxisY.Increment = NiceIncrement(memoryMaximum / 3);
        memoryGraph.Title = $"Working {current.WorkingSetMiB:F0} MiB · GC {current.GcHeapMiB:F0} MiB";
        FitGraph(memoryGraph, samples.Count, memoryMaximum);
    }

    private static void FitGraph(GraphView graph, int sampleCount, float yMaximum)
    {
        if (graph.Viewport.Width <= graph.MarginLeft || graph.Viewport.Height <= graph.MarginBottom)
        {
            return;
        }

        int width = Math.Max(1, graph.Viewport.Width - (int)graph.MarginLeft);
        int height = Math.Max(1, graph.Viewport.Height - (int)graph.MarginBottom);
        graph.CellSize = new PointF(
            Math.Max(1, (sampleCount - 1f) / width),
            Math.Max(0.1f, yMaximum / height));
        graph.ScrollOffset = PointF.Empty;
        graph.SetNeedsDraw();
    }

    private void UpdatePlayerList(OperationsSnapshot snapshot)
    {
        PlayerListRow[] rows = BuildPlayerRows(snapshot);
        string[] rendered = rows.Select(static row => row.Text).ToArray();
        if (playerRows.SequenceEqual(rendered, StringComparer.Ordinal))
        {
            playerListRows = rows;
            return;
        }

        PlayerId? selectedPlayer = playersList.SelectedItem is int selectedIndex &&
            (uint)selectedIndex < (uint)playerListRows.Count
            ? playerListRows[selectedIndex].Player?.Id
            : null;
        playerListRows = rows;
        playerRows = rendered;
        playersList.SetSource(new ObservableCollection<string>(rendered));
        if (selectedPlayer is PlayerId playerId)
        {
            int index = Array.FindIndex(rows, row => row.Player?.Id == playerId);
            if (index >= 0)
            {
                playersList.SelectedItem = index;
            }
        }
    }

    private static PlayerListRow[] BuildPlayerRows(OperationsSnapshot snapshot)
    {
        var rows = new List<PlayerListRow>();
        var knownWorlds = new HashSet<WorldId>();
        IEnumerable<WorldContextSnapshot> contexts = snapshot.WorldContexts
            .OrderBy(static context => context.Kind == WorldContextKind.Primary ? 0 : 1)
            .ThenBy(static context => context.DisplayName, StringComparer.OrdinalIgnoreCase);

        foreach (WorldContextSnapshot context in contexts)
        {
            knownWorlds.Add(context.Id);
            PlayerSnapshot[] members = snapshot.Players
                .Where(player => player.WorldId == context.Id)
                .OrderBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            rows.Add(new PlayerListRow(RenderWorldHeader(context, members.Length), null, context));
            rows.AddRange(members.Select(player => new PlayerListRow($"  {RenderPlayer(player)}", player, null)));
        }

        foreach (IGrouping<WorldId, PlayerSnapshot> unknown in snapshot.Players
                     .Where(player => !knownWorlds.Contains(player.WorldId))
                     .GroupBy(static player => player.WorldId))
        {
            PlayerSnapshot[] members = unknown.OrderBy(static player => player.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            rows.Add(new PlayerListRow($"◆ {unknown.Key} [UNKNOWN] · {members.Length}p", null, null));
            rows.AddRange(members.Select(player => new PlayerListRow($"  {RenderPlayer(player)}", player, null)));
        }

        return rows.ToArray();
    }

    private static string RenderWorldHeader(WorldContextSnapshot context, int playerCount)
    {
        string kind = context.Kind == WorldContextKind.Primary ? "PRIMARY" : "SANDBOX";
        string ttl = context.Kind == WorldContextKind.Sandbox && context.TimeToLiveRemaining > TimeSpan.Zero
            ? $" · ttl {FormatDuration(context.TimeToLiveRemaining)}"
            : string.Empty;
        return $"◆ {context.DisplayName} [{kind}] · {playerCount}p · {context.SimulationState}{ttl}";
    }

    private void UpdateModuleList(OperationsSnapshot snapshot)
    {
        string[] rows = snapshot.Modules.Select(module =>
            $"{StateGlyph(module.State)} {module.Name} {module.Version} {module.StartupDuration.TotalMilliseconds:F0}ms{FormatFailure(module.Failure)}").ToArray();
        if (moduleRows.SequenceEqual(rows, StringComparer.Ordinal))
        {
            return;
        }

        int? selected = modulesList.SelectedItem;
        moduleRows = rows;
        modulesList.SetSource(new ObservableCollection<string>(rows));
        if (selected is int index && index >= 0 && index < rows.Length)
        {
            modulesList.SelectedItem = index;
        }
    }

    private void UpdateRegionList(OperationsSnapshot snapshot)
    {
        string[] rows = snapshot.Regions.Select(RenderRegion).ToArray();
        if (regionRows.SequenceEqual(rows, StringComparer.Ordinal))
        {
            return;
        }

        int? selected = regionsList.SelectedItem;
        regionRows = rows;
        regionsList.SetSource(new ObservableCollection<string>(rows));
        if (selected is int index && index >= 0 && index < rows.Length)
        {
            regionsList.SelectedItem = index;
        }
    }

    private static string RenderRegion(RegionSnapshot region)
    {
        string owner = region.Owner ?? "none";
        string access = $"m{region.Members.Count}/g{region.Groups.Count}/f{region.Flags.Count}";
        return $"{region.WorldId} · {region.Name} · ({region.X},{region.Y}) {region.Width}x{region.Height} · p{region.Priority} · {owner} · {access} · r{region.Revision}";
    }

    private void UpdateModerationList(OperationsSnapshot snapshot)
    {
        string[] rows = snapshot.Bans
            .Select(static ban => $"BAN · {ban.Id} · {ban.Subject.Kind}:{ban.Subject.Value} · {ban.ExpiresAt?.ToString("u") ?? "never"} · {ban.Reason}")
            .Concat(snapshot.Mutes.Select(static mute =>
                $"MUTE · {mute.Id} · {mute.SubjectKind}:{mute.SubjectValue} · {mute.ExpiresAt:u} · {mute.Reason}"))
            .ToArray();
        if (moderationRows.SequenceEqual(rows, StringComparer.Ordinal)) return;
        int? selected = moderationList.SelectedItem;
        moderationRows = rows;
        moderationList.SetSource(new ObservableCollection<string>(rows));
        if (selected is int index && index >= 0 && index < rows.Length) moderationList.SelectedItem = index;
    }

    private static string RenderOverview(OperationsSnapshot snapshot)
    {
        ServerDashboardSnapshot server = snapshot.Server;
        string tps = server.OnlinePlayers == 0 && server.TickRate.Current <= 0
            ? "IDLE"
            : server.TickRate.Current.ToString("F1", CultureInfo.InvariantCulture);
        var text = new StringBuilder()
            .Append(server.ServerName).Append(' ').Append(server.Version).AppendLine()
            .Append(server.WorldName ?? "not loaded").Append(" · :").Append(server.ListenerPort).AppendLine()
            .Append("P ").Append(server.OnlinePlayers).Append('/').Append(server.MaximumPlayers)
            .Append(" · up ").Append(FormatDuration(server.Uptime)).AppendLine()
            .Append("TPS ").Append(tps)
            .Append(" · avg ").Append(server.TickRate.AverageOneMinute.ToString("F1", CultureInfo.InvariantCulture)).AppendLine()
            .Append("Interval p50/95/99 ")
            .Append(server.TickRate.TickDurationP50Milliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append('/')
            .Append(server.TickRate.TickDurationP95Milliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append('/')
            .Append(server.TickRate.TickDurationP99Milliseconds.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms");

        if (server.Warnings.Count > 0)
        {
            text.AppendLine().Append("! ").Append(string.Join(" | ", server.Warnings));
        }

        if (snapshot.Scenes.Count > 0 || snapshot.VisibilityLayers.Count > 0)
        {
            text.AppendLine()
                .Append("Scenes ").Append(snapshot.Scenes.Count)
                .Append(" · overlays ").Append(snapshot.Scenes.Sum(static scene => scene.Overlays.Count))
                .Append(" · conflicts ").Append(snapshot.Scenes.Sum(static scene => scene.ConflictCount))
                .Append(" · visibility ").Append(snapshot.VisibilityLayers.Count);
        }

        return text.ToString();
    }

    private static string RenderNetwork(OperationsSnapshot snapshot, OperationsMetricSample sample) =>
        $"IN  {sample.InboundKiBPerSecond,7:F1} KiB/s\n" +
        $"OUT {sample.OutboundKiBPerSecond,7:F1} KiB/s\n" +
        $"Pkts {snapshot.Network.InboundPackets:N0}/{snapshot.Network.OutboundPackets:N0}\n" +
        $"Total {FormatBytes(snapshot.Network.InboundBytes)} / {FormatBytes(snapshot.Network.OutboundBytes)}";

    private static string RenderPlayer(PlayerSnapshot player)
    {
        string session = player.ConnectedAt is DateTimeOffset connectedAt
            ? FormatDuration(DateTimeOffset.UtcNow - connectedAt)
            : "--";
        return $"[{player.Id.Slot,3}] {player.Name,-14} {player.Life,3}/{player.MaxLife,-3} {session,8}";
    }

    private static string RenderLogs(OperationsSnapshot snapshot, OperationsLogFilter filter)
    {
        bool explicitlyRequestsChat = filter.Source?.Contains("chat", StringComparison.OrdinalIgnoreCase) == true;
        IEnumerable<OperationsLogRecord> records = explicitlyRequestsChat
            ? snapshot.Logs
            : snapshot.Logs.Where(static record => !string.Equals(record.Source, "chat", StringComparison.OrdinalIgnoreCase));
        return string.Join(
            Environment.NewLine,
            records.Select(record =>
                $"{record.Timestamp:HH:mm:ss} {LevelGlyph(record.Level)} {record.Source,-14} {record.Text}"));
    }

    private static string RenderChat(OperationsSnapshot snapshot) => string.Join(
        Environment.NewLine,
        snapshot.Chat.Select(record => $"{record.Timestamp:HH:mm:ss} {record.Text}"));

    private static string RenderLogTitle(bool following, OperationsLogFilter filter) =>
        $"Logs [{(following ? "FOLLOW" : "PAUSED")} · {filter.MinimumLevel?.ToString() ?? "all"} · {filter.Source ?? "all"}]";

    private static Label CreateLabel() => new() { SchemeName = SchemeName };

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

    private static void SetLogText(
        string text,
        Editor editor,
        ref string appliedText,
        ref string pendingText)
    {
        if (string.Equals(appliedText, text, StringComparison.Ordinal))
        {
            pendingText = string.Empty;
            return;
        }

        pendingText = text;
        if (editor.Selection is not null)
        {
            return;
        }

        string next = pendingText;
        pendingText = string.Empty;
        TextDocument document = editor.Document ?? throw new InvalidOperationException("Log editor document is not initialized.");
        if (appliedText.Length > 0 && next.StartsWith(appliedText, StringComparison.Ordinal))
        {
            document.Insert(document.TextLength, next[appliedText.Length..]);
        }
        else
        {
            document.Text = next;
        }

        appliedText = next;
        editor.SetNeedsDraw();
    }

    private static TerminalUiPanelLayout CapturePanel(View view, int width, int height)
    {
        Rectangle frame = view.Frame;
        return new TerminalUiPanelLayout(
            view.Visible,
            ToPercent(frame.X, width),
            ToPercent(frame.Y, height),
            ToPercent(frame.Width, width),
            ToPercent(frame.Height, height));
    }

    private static int ToPercent(int value, int total) =>
        Math.Clamp((int)Math.Round(value * 100d / total), 0, 100);

    private static void DisposePopover(View owner, ref PopoverMenu? menu)
    {
        if (menu is null)
        {
            return;
        }

        owner.App?.Popovers?.DeRegister(menu);
        menu.Dispose();
        menu = null;
    }

    private static string QuoteArgument(string value) =>
        value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

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

    private static string FormatDuration(TimeSpan value) =>
        value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value:hh\\:mm\\:ss}"
            : value.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);

    private static string FormatFailure(string? failure) =>
        string.IsNullOrWhiteSpace(failure) ? string.Empty : $" · {failure}";

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

    private sealed record PlayerListRow(
        string Text,
        PlayerSnapshot? Player,
        WorldContextSnapshot? Context);

    private sealed record WindowInteraction(Rectangle Frame, bool StartedOnTitle);
}
