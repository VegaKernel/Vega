using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Admin.Client;
using Vega.Admin.Contracts;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

/// <summary>
/// Phase 27 workspace shell built on the Terminal.Gui v2.4 <see cref="Tabs"/> API.
/// The first workspace hosts the existing tiled dashboard while the remaining workspaces
/// establish stable UI destinations that can migrate to Admin Contracts independently.
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
    private readonly ListView commandList = CreateList();
    private readonly Label commandDetails = CreateFillLabel();
    private readonly ListView modulesList = CreateList();
    private readonly ListView logsList = CreateList();
    private readonly ListView chatList = CreateList();
    private readonly Label configurationText = CreateFillLabel();
    private readonly Label debugText = CreateFillLabel();
    private readonly ListView administrationList = CreateList();
    private IReadOnlyList<AdminCommandDescriptor> commands = Array.Empty<AdminCommandDescriptor>();
    private Task<IReadOnlyList<AdminCommandDescriptor>>? commandCatalogCapture;
    private DateTimeOffset nextCatalogRefreshAt = DateTimeOffset.MinValue;
    private string? commandCatalogFailure;
    private long chordDeadline;

    public AdminWorkspaceTabs(View dashboardDesktop, IVegaAdminClient adminClient)
    {
        ArgumentNullException.ThrowIfNull(dashboardDesktop);
        this.adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));

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
        View configuration = CreateWorkspace("7 Config", configurationText);
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

        commandList.ValueChanged += (_, _) => UpdateCommandDetails();
        configurationText.Text =
            "Plugin configuration workspace\n\n" +
            "Waiting for the Phase 27 registered configuration descriptor capability.\n" +
            "No arbitrary remote filesystem editor will be exposed here.";
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

        tabs.Value = workspaceViews[zeroBasedIndex];
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

    public void Update(OperationsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        PumpCommandCatalog();
        SetRows(playersList, snapshot.Players
            .OrderBy(static player => player.WorldId.ToString(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static player => player.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static player =>
                $"[{player.Id.Slot,3}] {player.Name,-20} · {player.WorldId} · HP {player.Life}/{player.MaxLife} · MP {player.Mana}/{player.MaxMana} · team {player.Team} · pvp {(player.IsHostile ? "on" : "off")}")
            .ToArray());
        SetRows(modulesList, snapshot.Modules
            .OrderBy(static module => module.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static module =>
                $"{module.State,-10} · {module.Name} {module.Version} · {module.StartupDuration.TotalMilliseconds:F0} ms" +
                (string.IsNullOrWhiteSpace(module.Failure) ? string.Empty : $" · {module.Failure}"))
            .ToArray());
        SetRows(logsList, snapshot.Logs
            .Select(static record =>
                $"{record.Timestamp:HH:mm:ss} {record.Level,-11} {record.Source}: {record.Text}")
            .ToArray());
        SetRows(chatList, snapshot.Chat
            .Select(static record => $"{record.Timestamp:HH:mm:ss} {record.Text}")
            .ToArray());
        SetRows(administrationList, RenderAdministration(snapshot.Administration));
        debugText.Text = RenderDebug(snapshot);
    }

    private View CreateCommandsWorkspace()
    {
        var workspace = new View
        {
            Title = "3 Commands",
            SchemeName = SchemeName
        };
        commandList.X = 0;
        commandList.Y = 0;
        commandList.Width = Dim.Percent(38);
        commandList.Height = Dim.Fill();
        commandDetails.X = Pos.Right(commandList) + 1;
        commandDetails.Y = 0;
        commandDetails.Width = Dim.Fill();
        commandDetails.Height = Dim.Fill();
        workspace.Add(commandList, commandDetails);
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
            SetRows(commandList, commands.Select(static descriptor =>
                $"{descriptor.Owner,-16} {descriptor.Name,-18} {descriptor.Syntax}").ToArray());
            UpdateCommandDetails();
            return;
        }

        commandCatalogFailure = completed.Exception?.GetBaseException().Message ?? "Command catalog unavailable.";
        SetRows(commandList, [$"ERROR · {commandCatalogFailure}"]);
        commandDetails.Text = commandCatalogFailure;
    }

    private void UpdateCommandDetails()
    {
        if (commandCatalogFailure is not null)
        {
            commandDetails.Text = commandCatalogFailure;
            return;
        }

        int index = commandList.SelectedItem ?? -1;
        if ((uint)index >= (uint)commands.Count)
        {
            commandDetails.Text = commands.Count == 0
                ? "Loading command catalog through IVegaAdminClient..."
                : "Select a command to inspect its Admin descriptor.";
            return;
        }

        AdminCommandDescriptor descriptor = commands[index];
        var text = new StringBuilder()
            .AppendLine(descriptor.FallbackTitle ?? descriptor.Name)
            .AppendLine()
            .Append("Owner: ").AppendLine(descriptor.Owner)
            .Append("Syntax: ").AppendLine(descriptor.Syntax)
            .Append("Aliases: ").AppendLine(descriptor.Aliases.Count == 0 ? "none" : string.Join(", ", descriptor.Aliases))
            .Append("Permissions: ").AppendLine(descriptor.Permissions.Count == 0 ? "none" : string.Join(", ", descriptor.Permissions))
            .Append("Server console: ").AppendLine(descriptor.AllowServer ? "yes" : "no");
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

    private static string[] RenderAdministration(AdministrationOperationsSnapshot administration)
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

    private static string RenderDebug(OperationsSnapshot snapshot)
    {
        ServerDashboardSnapshot server = snapshot.Server;
        RuntimeMetricsSnapshot runtime = snapshot.Runtime;
        NetworkMetricsSnapshot network = snapshot.Network;
        return
            $"Debug / metrics workspace\n\n" +
            $"TPS       current {server.TickRate.Current:F1} · avg1m {server.TickRate.AverageOneMinute:F1} · p95 {server.TickRate.TickDurationP95Milliseconds:F1} ms · p99 {server.TickRate.TickDurationP99Milliseconds:F1} ms\n" +
            $"Memory    working {runtime.Host.WorkingSetBytes / 1024d / 1024d:F1} MiB · managed {runtime.ManagedMemoryBytes / 1024d / 1024d:F1} MiB · GC heap {runtime.GcHeapBytes / 1024d / 1024d:F1} MiB\n" +
            $"GC        gen0 {runtime.Gen0Collections} · gen1 {runtime.Gen1Collections} · gen2 {runtime.Gen2Collections}\n" +
            $"Network   in {network.InboundPackets:N0}/{network.InboundBytes:N0} B · out {network.OutboundPackets:N0}/{network.OutboundBytes:N0} B\n" +
            $"Security  rejected {network.RejectedInboundPackets:N0} · violations {network.SecurityViolations:N0}\n\n" +
            "This screen currently reuses the bounded local Operations projection. Its remote equivalent will move behind the Phase 27 debug/metrics Admin capability before remote transport is enabled.";
    }

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
}
