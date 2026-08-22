using System.Globalization;
using System.Text;
using Vega.Contracts.Operations;
using Vega.Contracts.Regions;

namespace Vega.TerminalUI;

public sealed class OperationsDashboardViewModel(
    IOperationsSnapshotProvider snapshots,
    IOperationsCommandService commands,
    IUnhandledOperationsCommandSink? unhandledCommands = null)
{
    private OperationsLogFilter logFilter = new(Limit: 100);
    private OperationsSnapshot? displayed;
    private IReadOnlyList<OperationsLogRecord>? pausedLogs;

    public bool IsFollowing { get; private set; } = true;

    public OperationsLogFilter LogFilter => logFilter;

    public OperationsSnapshot Refresh()
    {
        OperationsSnapshot current = snapshots.Capture(
            logFilter,
            OperationsSnapshotExposure.Sensitive);
        if (IsFollowing)
        {
            pausedLogs = null;
            displayed = current;
            return current;
        }

        pausedLogs ??= displayed?.Logs.ToArray() ?? current.Logs.ToArray();
        displayed = current with { Logs = pausedLogs };
        return displayed;
    }

    public string Render() => OperationsDashboardRenderer.Render(displayed ?? Refresh(), IsFollowing, logFilter);

    public bool ToggleFollowing()
    {
        SetFollowing(!IsFollowing);
        return IsFollowing;
    }

    public void SetFollowing(bool value)
    {
        if (IsFollowing == value)
        {
            return;
        }

        IsFollowing = value;
        pausedLogs = value ? null : displayed?.Logs.ToArray();
    }

    public void SetLogFilter(OperationsLogLevel? minimumLevel, string? source, string? text)
    {
        logFilter = logFilter with
        {
            MinimumLevel = minimumLevel,
            Source = NormalizeFilterValue(source),
            Text = NormalizeFilterValue(text)
        };
        pausedLogs = null;
        displayed = null;
    }

    public void ClearLogFilters()
    {
        logFilter = new OperationsLogFilter(Limit: logFilter.Limit);
        pausedLogs = null;
        displayed = null;
    }

    public async ValueTask<string> ExecutePaletteAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        string input = text.Trim();
        if (input.Length == 0)
        {
            return "Enter a command or palette action.";
        }

        if (!input.StartsWith(':'))
        {
            OperationsActionResult result = await commands
                .ExecuteAsync(input, cancellationToken)
                .ConfigureAwait(false);
            if (result.ErrorCode == "command.not_found" && unhandledCommands is not null)
            {
                await unhandledCommands.SubmitAsync(input, cancellationToken).ConfigureAwait(false);
                return "Submitted to the Terraria server console.";
            }

            string message = result.Messages.Count > 0
                ? string.Join(" | ", result.Messages)
                : result.ErrorCode ?? result.Status.ToString();
            return $"{result.Status}: {message}";
        }

        string[] parts = input[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string action = parts.ElementAtOrDefault(0)?.ToLowerInvariant() ?? string.Empty;
        string value = parts.ElementAtOrDefault(1)?.Trim() ?? string.Empty;

        switch (action)
        {
            case "pause":
                SetFollowing(false);
                return "Log following paused.";
            case "follow":
                SetFollowing(true);
                return "Log following enabled.";
            case "level":
                SetLogFilter(ParseLevel(value), logFilter.Source, logFilter.Text);
                return $"Minimum log level: {logFilter.MinimumLevel?.ToString() ?? "all"}.";
            case "source":
                SetLogFilter(logFilter.MinimumLevel, value, logFilter.Text);
                return $"Log source filter: {logFilter.Source ?? "all"}.";
            case "find":
                SetLogFilter(logFilter.MinimumLevel, logFilter.Source, value);
                return $"Log text filter: {logFilter.Text ?? "all"}.";
            case "clear":
                ClearLogFilters();
                return "Log filters cleared.";
            default:
                return "Palette actions: :pause, :follow, :level <level|all>, :source <text>, :find <text>, :clear.";
        }
    }

    private static OperationsLogLevel? ParseLevel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Enum.TryParse(value, true, out OperationsLogLevel level)
            ? level
            : throw new ArgumentException($"Unknown log level '{value}'.");
    }

    private static string? NormalizeFilterValue(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value.Trim();
}

public static class OperationsDashboardRenderer
{
    public static string Render(
        OperationsSnapshot snapshot,
        bool isFollowing,
        OperationsLogFilter filter)
    {
        var text = new StringBuilder();
        ServerDashboardSnapshot server = snapshot.Server;

        text.AppendLine($"SERVER  {server.ServerName} {server.Version}  {server.LifecycleStage}  uptime {FormatDuration(server.Uptime)}");
        text.AppendLine($"WORLD   {server.WorldName ?? "not loaded"}  ready={server.IsWorldReady}  network={server.IsNetworkReady}  port={server.ListenerPort}");
        text.AppendLine($"PLAYERS {server.OnlinePlayers}/{server.MaximumPlayers}  TPS={server.TickRate.Current:F1} avg1m={server.TickRate.AverageOneMinute:F1} min={server.TickRate.Minimum:F1} max={server.TickRate.Maximum:F1}");
        text.AppendLine($"TICKMS  p50={server.TickRate.TickDurationP50Milliseconds:F1} p95={server.TickRate.TickDurationP95Milliseconds:F1} p99={server.TickRate.TickDurationP99Milliseconds:F1}");
        text.AppendLine($"MEMORY  working={FormatBytes(snapshot.Runtime.Host.WorkingSetBytes)} managed={FormatBytes(snapshot.Runtime.ManagedMemoryBytes)} heap={FormatBytes(snapshot.Runtime.GcHeapBytes)}");
        text.AppendLine($"GC      gen0={snapshot.Runtime.Gen0Collections} gen1={snapshot.Runtime.Gen1Collections} gen2={snapshot.Runtime.Gen2Collections} fragmented={FormatBytes(snapshot.Runtime.GcFragmentedBytes)}");
        text.AppendLine($"NETWORK in={snapshot.Network.InboundPackets:N0}/{FormatBytes(snapshot.Network.InboundBytes)} out={snapshot.Network.OutboundPackets:N0}/{FormatBytes(snapshot.Network.OutboundBytes)}");
        if (snapshot.WorldClocks.FirstOrDefault() is { } clock)
        {
            text.AppendLine($"CLOCK   {(clock.Predicted.IsDaytime ? "day" : "night")} time={clock.Predicted.TimeTicks / 60d:F1}s drift={clock.DriftTicks / 60d:+0.00;-0.00;0.00}s corrections={clock.CorrectionCount} reanchors={clock.ReanchorCount}");
        }

        if (server.Warnings.Count > 0)
        {
            text.AppendLine("WARNINGS " + string.Join(" | ", server.Warnings));
        }

        text.AppendLine();
        text.AppendLine("PLAYERS");
        if (snapshot.Players.Count == 0)
        {
            text.AppendLine("  no connected players");
        }
        else
        {
            foreach (var player in snapshot.Players)
            {
                text.AppendLine($"  [{player.Id.Slot,3}] {player.Name,-20} HP {player.Life,4}/{player.MaxLife,-4} MP {player.Mana,3}/{player.MaxMana,-3} team={player.Team} pvp={player.IsHostile}");
            }
        }

        text.AppendLine();
        text.AppendLine("MODULES");
        text.AppendLine($"  hot reload: {(snapshot.ModuleDirectory.IsHotReloadEnabled ? "enabled" : "disabled")}");
        if (snapshot.ModuleDirectory.IsRestartRequired)
        {
            text.AppendLine($"  RESTART REQUIRED: {string.Join(", ", snapshot.ModuleDirectory.ChangedFiles)}");
        }
        foreach (ModuleOperationsSnapshot module in snapshot.Modules)
        {
            text.AppendLine($"  {module.Name,-22} {module.Version,-12} {module.State,-10} {module.StartupDuration.TotalMilliseconds,7:F1} ms deps=[{string.Join(",", module.Dependencies)}]{FormatFailure(module.Failure)}");
        }

        text.AppendLine();
        text.AppendLine("REGIONS");
        if (snapshot.Regions.Count == 0)
        {
            text.AppendLine("  no configured regions");
        }
        else
        {
            foreach (RegionSnapshot region in snapshot.Regions)
            {
                text.AppendLine($"  {region.WorldId}/{region.Name} ({region.X},{region.Y}) {region.Width}x{region.Height} priority={region.Priority} owner={region.Owner ?? "none"} members={region.Members.Count} groups={region.Groups.Count} revision={region.Revision}");
            }
        }

        text.AppendLine();
        text.AppendLine($"LOGS {(isFollowing ? "FOLLOW" : "PAUSED")} level={filter.MinimumLevel?.ToString() ?? "all"} source={filter.Source ?? "all"} text={filter.Text ?? "all"}");
        foreach (OperationsLogRecord record in snapshot.Logs)
        {
            text.AppendLine($"  {record.Timestamp:HH:mm:ss} {record.Level,-11} {record.Source}: {record.Text}");
        }

        return text.ToString();
    }

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
        string.IsNullOrWhiteSpace(failure) ? string.Empty : $" failure={failure}";
}
