using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Admin.Client;
using Vega.Admin.Contracts;

namespace Vega.TerminalUI;

/// <summary>
/// Phase 27 workspace shell built on the Terminal.Gui v2.4 <see cref="Tabs"/> API.
/// The first workspace hosts the existing tiled dashboard. Dedicated administration workspaces
/// query only their own transport-neutral projection through <see cref="IVegaAdminClient"/>.
/// </summary>
internal sealed class AdminWorkspaceTabs
{
    private const string SchemeName = "VegaDark";
    private const long ChordWindowMilliseconds = 900;
    private readonly IVegaAdminClient adminClient;
    private readonly Tabs tabs = new()
    {
        X = 0,
        Y = 1,
        Width = Dim.Fill(),
        Height = Dim.Fill(5),
        SchemeName = SchemeName,
        Visible = false
    };
    private readonly View[] workspaceViews;
    private readonly ListView playersList = CreateList();
    private readonly TextField commandSearch = new()
    {
        SchemeName = SchemeName,
        Text = string.Empty
    };
    private readonly ListView commandList = CreateList();
    private readonly Label commandDetails = CreateFillLabel();
    private readonly ListView modulesList = CreateList();
    private readonly ListView logsList = CreateList();
    private readonly ListView chatList = CreateList();
    private readonly AdminConfigurationWorkspace configurationWorkspace;
    private readonly Label debugText = CreateFillLabel();
    private readonly ListView administrationList = CreateList();
    private IReadOnlyList<AdminCommandDescriptor> commands = Array.Empty<AdminCommandDescriptor>();
    private IReadOnlyList<AdminCommandDescriptor> visibleCommands = Array.Empty<AdminCommandDescriptor>();
    private Task<IReadOnlyList<AdminCommandDescriptor>>? commandCatalogCapture;
    private Task<WorkspaceResult>? workspaceCapture;
    private int workspaceCaptureIndex = -1;
    private DateTimeOffset nextCatalogRefreshAt = DateTimeOffset.MinValue;
    private DateTimeOffset nextWorkspaceRefreshAt = DateTimeOffset.MinValue;
    private string? commandCatalogFailure;
    private long chordDeadline;

    public AdminWorkspaceTabs(View dashboardDesktop, IVegaAdminClient adminClient)
    {
        ArgumentNullException.ThrowIfNull(dashboardDesktop);
        this.adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));
        configurationWorkspace = new AdminConfigurationWorkspace(adminClient);

        dashboardDesktop.X = 0;
        dashboardDesktop.Y = 0;
        dashboardDesktop.Width = Dim.Fill();
        dashboardDesktop.Height = Dim.Fill();
        dashboardDesktop.Visible = true;

        View dashboard = CreateWorkspace("1 Dashboard", dashboardDesktop);
        View players = CreateWorkspace("2 Players", playersList);
        View commandsWorkspace = CreateCommandsWorkspace();
        View modules = CreateWorkspace("4 Modules", modulesList);
        View logs = CreateWorkspace("5 Logs", logsList);
        View chat = CreateWorkspace("6 Chat", chatList);
        View configuration = CreateWorkspace("7 Config", configurationWorkspace.Root);
        View debug = CreateWorkspace("8 Debug", debugText);
        View administration = CreateWorkspace("9 Admin", administrationList);
        workspaceViews =
        [
            dashboard,
            players,
            commandsWorkspace,
            modules,
            logs,
            chat,
            configuration,
            debug,
            administration
        ];
        tabs.Add(workspaceViews);
        tabs.Value = dashboard;

        commandSearch.TextChanging += (_, args) => ApplyCommandFilter(args.Result ?? string.Empty);
        commandList.ValueChanged += (_, _) => UpdateCommandDetails();
    }

    public View Root => tabs;

    public bool Visible
    {
        get => tabs.Visible;
        set => tabs.Visible = value;
    }

    public int SelectedWorkspaceIndex => tabs.Value is null ? 0 : Math.Max(0, Array.IndexOf(workspaceViews, tabs.Value));

    public string SelectedWorkspaceTitle => workspaceViews[SelectedWorkspaceIndex].Title?.ToString() ?? "Dashboard";

    public bool SelectWorkspace(int zeroBasedIndex)
    {
        if ((uint)zeroBasedIndex >= (uint)workspaceViews.Length)
        {
            return false;
        }

        if (tabs.Value != workspaceViews[zeroBasedIndex])
        {
            tabs.Value = workspaceViews[zeroBasedIndex];
            nextWorkspaceRefreshAt = DateTimeOffset.MinValue;
            if (zeroBasedIndex == 6)
            {
                configurationWorkspace.RequestRefresh();
            }
        }

        return true;
    }

    public void FocusLogs()
    {
        SelectWorkspace(4);
        logsList.SetFocus();
    }

    public void FocusAdministration()
    {
        SelectWorkspace(8);
        administrationList.SetFocus();
    }

    /// <summary>
    /// Implements the portable terminal equivalent of "Tab+digit": Tab arms a short-lived
    /// chord without consuming normal focus traversal, then 1..9 selects a workspace.
    /// </summary>
    public bool HandleWorkspaceChord(Key key, bool allowArm = true)
    {
        ArgumentNullException.ThrowIfNull(key);
        long now = Environment.TickCount64;
        if (allowArm && key.Equals(Key.Tab))
        {
            chordDeadline = now + ChordWindowMilliseconds;
            return false;
        }

        if (chordDeadline == 0 || now > chordDeadline)
        {
            chordDeadline = 0;
            return false;
        }

        chordDeadline = 0;
        string grapheme = key.AsGrapheme;
        if (grapheme.Length != 1 || grapheme[0] is < '1' or > '9')
        {
            return false;
        }

        bool selected = SelectWorkspace(grapheme[0] - '1');
        if (selected)
        {
            key.Handled = true;
        }

        return selected;
    }

    /// <summary>
    /// Pumps only the selected administration projection. The legacy dashboard is refreshed by
    /// its existing local Operations path until that workspace is migrated in a separate slice.
    /// </summary>
    public void Pulse()
    {
        int selected = SelectedWorkspaceIndex;
        if (selected == 2)
        {
            PumpCommandCatalog();
            return;
        }

        if (selected == 6)
        {
            configurationWorkspace.Pulse();
            return;
        }

        if (selected == 0)
        {
            return;
        }

        PumpWorkspace(selected);
    }

    private View CreateCommandsWorkspace()
    {
        var workspace = new View
        {
            Title = "3 Commands",
            SchemeName = SchemeName
        };
        var searchLabel = new Label
        {
            Text = "Search",
            X = 0,
            Y = 0,
            SchemeName = SchemeName
        };
        commandSearch.X = Pos.Right(searchLabel) + 1;
        commandSearch.Y = 0;
        commandSearch.Width = Dim.Percent(30);
        commandList.X = 0;
        commandList.Y = 1;
        commandList.Width = Dim.Percent(38);
        commandList.Height = Dim.Fill();
        commandDetails.X = Pos.Right(commandList) + 1;
        commandDetails.Y = 0;
        commandDetails.Width = Dim.Fill();
        commandDetails.Height = Dim.Fill();
        workspace.Add(searchLabel, commandSearch, commandList, commandDetails);
        return workspace;
    }

    private static View CreateWorkspace(string title, View content)
    {
        var workspace = new View
        {
            Title = title,
            SchemeName = SchemeName
        };
        content.X = 0;
        content.Y = 0;
        content.Width = Dim.Fill();
        content.Height = Dim.Fill();
        workspace.Add(content);
        return workspace;
    }

    private void PumpWorkspace(int selected)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (workspaceCapture is null && now >= nextWorkspaceRefreshAt)
        {
            workspaceCapture = CaptureWorkspaceAsync(selected);
            workspaceCaptureIndex = selected;
        }

        if (workspaceCapture is null || !workspaceCapture.IsCompleted)
        {
            return;
        }

        Task<WorkspaceResult> completed = workspaceCapture;
        int completedIndex = workspaceCaptureIndex;
        workspaceCapture = null;
        workspaceCaptureIndex = -1;

        if (completedIndex != SelectedWorkspaceIndex)
        {
            nextWorkspaceRefreshAt = DateTimeOffset.MinValue;
            return;
        }

        nextWorkspaceRefreshAt = now + RefreshIntervalFor(completedIndex);
        if (!completed.IsCompletedSuccessfully)
        {
            RenderWorkspaceFailure(completedIndex, completed.Exception?.GetBaseException().Message ?? "Admin query unavailable.");
            return;
        }

        WorkspaceResult result = completed.GetAwaiter().GetResult();
        if (result.Index != SelectedWorkspaceIndex)
        {
            nextWorkspaceRefreshAt = DateTimeOffset.MinValue;
            return;
        }

        ApplyWorkspaceResult(result);
    }

    private async Task<WorkspaceResult> CaptureWorkspaceAsync(int selected)
    {
        return selected switch
        {
            1 => new PlayersWorkspaceResult(await adminClient.GetPlayersAsync().ConfigureAwait(false)),
            3 => new ModulesWorkspaceResult(await adminClient.GetModulesAsync().ConfigureAwait(false)),
            4 => new LogsWorkspaceResult(await adminClient.QueryLogsAsync(new AdminLogQuery(Limit: 200)).ConfigureAwait(false)),
            5 => new ChatWorkspaceResult(await adminClient.GetChatAsync(200).ConfigureAwait(false)),
            7 => new DiagnosticsWorkspaceResult(await adminClient.GetDiagnosticsAsync().ConfigureAwait(false)),
            8 => new AdministrationWorkspaceResult(await adminClient.GetAdministrationAsync().ConfigureAwait(false)),
            _ => new EmptyWorkspaceResult(selected)
        };
    }

    private void ApplyWorkspaceResult(WorkspaceResult result)
    {
        switch (result)
        {
            case PlayersWorkspaceResult players:
                SetRows(playersList, players.Value
                    .OrderBy(static player => player.WorldId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static player =>
                        $"[{player.Slot,3}] {player.Name,-20} · {player.WorldId} · HP {player.Life}/{player.MaxLife} · MP {player.Mana}/{player.MaxMana} · {player.Platform} · team {player.Team} · pvp {(player.IsHostile ? "on" : "off")}")
                    .ToArray());
                break;
            case ModulesWorkspaceResult modules:
                SetRows(modulesList, modules.Value
                    .OrderBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static module =>
                        $"{module.State,-10} · {module.Name} {module.Version} · {module.StartupMilliseconds} ms" +
                        (string.IsNullOrWhiteSpace(module.Failure) ? string.Empty : $" · {module.Failure}"))
                    .ToArray());
                break;
            case LogsWorkspaceResult logs:
                SetRows(logsList, logs.Value.Select(static record =>
                    $"{record.Timestamp:HH:mm:ss} {record.Level,-11} {record.Source}: {record.Text}").ToArray());
                break;
            case ChatWorkspaceResult chat:
                SetRows(chatList, chat.Value.Select(static record =>
                    $"{record.Timestamp:HH:mm:ss} {record.Source}: {record.Text}").ToArray());
                break;
            case DiagnosticsWorkspaceResult diagnostics:
                debugText.Text = RenderDebug(diagnostics.Value);
                break;
            case AdministrationWorkspaceResult administration:
                SetRows(administrationList, RenderAdministration(administration.Value));
                break;
        }
    }

    private void RenderWorkspaceFailure(int selected, string message)
    {
        string text = $"ERROR · {message}";
        switch (selected)
        {
            case 1:
                SetRows(playersList, [text]);
                break;
            case 3:
                SetRows(modulesList, [text]);
                break;
            case 4:
                SetRows(logsList, [text]);
                break;
            case 5:
                SetRows(chatList, [text]);
                break;
            case 7:
                debugText.Text = text;
                break;
            case 8:
                SetRows(administrationList, [text]);
                break;
        }
    }

    private static TimeSpan RefreshIntervalFor(int selected) => selected switch
    {
        4 or 5 => TimeSpan.FromMilliseconds(500),
        1 or 7 => TimeSpan.FromSeconds(1),
        3 => TimeSpan.FromSeconds(2),
        8 => TimeSpan.FromSeconds(3),
        _ => TimeSpan.FromSeconds(1)
    };

    private void PumpCommandCatalog()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (commandCatalogCapture is null && now >= nextCatalogRefreshAt)
        {
            commandCatalogCapture = adminClient.GetCommandCatalogAsync().AsTask();
        }

        if (commandCatalogCapture is null || !commandCatalogCapture.IsCompleted)
        {
            return;
        }

        Task<IReadOnlyList<AdminCommandDescriptor>> completed = commandCatalogCapture;
        commandCatalogCapture = null;
        nextCatalogRefreshAt = now.AddSeconds(5);
        if (completed.IsCompletedSuccessfully)
        {
            commands = completed.GetAwaiter().GetResult()
                .OrderBy(static descriptor => descriptor.Owner, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            commandCatalogFailure = null;
            ApplyCommandFilter(commandSearch.Text);
            return;
        }

        commandCatalogFailure = completed.Exception?.GetBaseException().Message ?? "Command catalog unavailable.";
        visibleCommands = Array.Empty<AdminCommandDescriptor>();
        SetRows(commandList, [$"ERROR · {commandCatalogFailure}"]);
        commandDetails.Text = commandCatalogFailure;
    }

    private void ApplyCommandFilter(string value)
    {
        commandCatalogFailure = null;
        string query = value.Trim();
        visibleCommands = query.Length == 0
            ? commands
            : commands.Where(descriptor => MatchesCommand(descriptor, query)).ToArray();
        SetRows(commandList, visibleCommands.Select(static descriptor =>
            $"{descriptor.Owner,-16} {descriptor.Name,-18} {descriptor.Syntax}").ToArray());
        if (visibleCommands.Count > 0)
        {
            commandList.SelectedItem = 0;
        }

        UpdateCommandDetails();
    }

    private static bool MatchesCommand(AdminCommandDescriptor descriptor, string query)
    {
        bool Contains(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        return Contains(descriptor.Name) ||
            Contains(descriptor.Owner) ||
            Contains(descriptor.Syntax) ||
            Contains(descriptor.FallbackTitle) ||
            Contains(descriptor.FallbackDescription) ||
            Contains(descriptor.FallbackHelp) ||
            descriptor.Aliases.Any(Contains) ||
            descriptor.Permissions.Any(Contains);
    }

    private void UpdateCommandDetails()
    {
        if (commandCatalogFailure is not null)
        {
            commandDetails.Text = commandCatalogFailure;
            return;
        }

        int index = commandList.SelectedItem ?? -1;
        if ((uint)index >= (uint)visibleCommands.Count)
        {
            commandDetails.Text = commands.Count == 0
                ? "Loading command catalog through IVegaAdminClient..."
                : "No matching command selected.";
            return;
        }

        AdminCommandDescriptor descriptor = visibleCommands[index];
        var text = new StringBuilder()
            .AppendLine(descriptor.FallbackTitle ?? descriptor.Name)
            .AppendLine()
            .Append("Owner: ").AppendLine(descriptor.Owner)
            .Append("Syntax: ").AppendLine(descriptor.Syntax)
            .Append("Aliases: ").AppendLine(descriptor.Aliases.Count == 0 ? "none" : string.Join(", ", descriptor.Aliases))
            .Append("Permissions: ").AppendLine(descriptor.Permissions.Count == 0 ? "none" : string.Join(", ", descriptor.Permissions))
            .Append("Server console: ").AppendLine(descriptor.AllowServer ? "yes" : "no")
            .Append("Players: ").AppendLine(descriptor.AllowPlayers ? "yes" : "no");
        if (!string.IsNullOrWhiteSpace(descriptor.FallbackDescription))
        {
            text.AppendLine().AppendLine(descriptor.FallbackDescription);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.FallbackHelp))
        {
            text.AppendLine().AppendLine(descriptor.FallbackHelp);
        }

        commandDetails.Text = text.ToString();
    }

    private static string[] RenderAdministration(AdminAdministrationSnapshot administration)
    {
        IEnumerable<string> groups = administration.Groups
            .OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
                $"GROUP · {group.Name} · parent={group.ParentName ?? "none"} · accounts={group.AccountCount} · +{group.GrantedPermissions.Count}/-{group.DeniedPermissions.Count} · r{group.Revision}");
        IEnumerable<string> accounts = administration.Accounts
            .OrderBy(static account => account.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static account =>
                $"ACCOUNT · {account.Name} · group={account.GroupName} · culture={account.Culture ?? "default"} · last={account.LastSeenUtc?.ToString("u") ?? "never"}");
        return groups.Concat(accounts).DefaultIfEmpty("No administration records exposed.").ToArray();
    }

    private static string RenderDebug(AdminDiagnosticsSnapshot diagnostics) =>
        $"Debug / metrics workspace\n\n" +
        $"TPS       current {diagnostics.CurrentTicksPerSecond:F1} · avg1m {diagnostics.AverageTicksPerSecondOneMinute:F1} · p50 {diagnostics.TickDurationP50Milliseconds:F1} ms · p95 {diagnostics.TickDurationP95Milliseconds:F1} ms · p99 {diagnostics.TickDurationP99Milliseconds:F1} ms\n" +
        $"Memory    working {diagnostics.WorkingSetBytes / 1024d / 1024d:F1} MiB · managed {diagnostics.ManagedMemoryBytes / 1024d / 1024d:F1} MiB · GC heap {diagnostics.GcHeapBytes / 1024d / 1024d:F1} MiB · fragmented {diagnostics.GcFragmentedBytes / 1024d / 1024d:F1} MiB\n" +
        $"GC        gen0 {diagnostics.Gen0Collections} · gen1 {diagnostics.Gen1Collections} · gen2 {diagnostics.Gen2Collections}\n" +
        $"Threads   worker {diagnostics.AvailableWorkerThreads}/{diagnostics.MaximumWorkerThreads}\n" +
        $"Network   in {diagnostics.InboundPackets:N0}/{diagnostics.InboundBytes:N0} B · out {diagnostics.OutboundPackets:N0}/{diagnostics.OutboundBytes:N0} B\n" +
        $"Security  rejected {diagnostics.RejectedInboundPackets:N0} · violations {diagnostics.SecurityViolations:N0}\n\n" +
        "This workspace now consumes only the transport-neutral diagnostics Admin projection.";

    private static ListView CreateList() => new()
    {
        SchemeName = SchemeName,
        ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar
    };

    private static Label CreateFillLabel() => new()
    {
        SchemeName = SchemeName,
        Width = Dim.Fill(),
        Height = Dim.Fill()
    };

    private static void SetRows(ListView list, IReadOnlyList<string> rows)
    {
        int? selected = list.SelectedItem;
        list.SetSource(new ObservableCollection<string>(rows.Count == 0 ? ["(empty)"] : rows.ToArray()));
        if (selected is int index && index >= 0 && list.Source is { } source && index < source.Count)
        {
            list.SelectedItem = index;
        }
    }

    private abstract record WorkspaceResult(int Index);

    private sealed record EmptyWorkspaceResult(int Selected) : WorkspaceResult(Selected);

    private sealed record PlayersWorkspaceResult(IReadOnlyList<AdminPlayerSnapshot> Value) : WorkspaceResult(1);

    private sealed record ModulesWorkspaceResult(IReadOnlyList<AdminModuleSnapshot> Value) : WorkspaceResult(3);

    private sealed record LogsWorkspaceResult(IReadOnlyList<AdminLogRecord> Value) : WorkspaceResult(4);

    private sealed record ChatWorkspaceResult(IReadOnlyList<AdminChatRecord> Value) : WorkspaceResult(5);

    private sealed record DiagnosticsWorkspaceResult(AdminDiagnosticsSnapshot Value) : WorkspaceResult(7);

    private sealed record AdministrationWorkspaceResult(AdminAdministrationSnapshot Value) : WorkspaceResult(8);
}
