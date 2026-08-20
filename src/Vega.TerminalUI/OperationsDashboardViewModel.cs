using System.Globalization;
using System.Text;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

public sealed class OperationsDashboardViewModel(
    IOperationsSnapshotProvider snapshots,
    IOperationsCommandService commands)
{
    private OperationsLogFilter logFilter = new(Limit: 100);
    private OperationsSnapshot? displayed;

    public bool IsFollowing { get; private set; } = true;

    public OperationsSnapshot Refresh()
    {
        if (IsFollowing || displayed is null)
        {
            displayed = snapshots.Capture(logFilter);
        }

        return displayed;
    }

    public string Render() => OperationsDashboardRenderer.Render(Refresh(), IsFollowing, logFilter);

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
                IsFollowing = false;
                return "Log following paused.";
            case "follow":
                IsFollowing = true;
                return "Log following enabled.";
            case "level":
                logFilter = logFilter with { MinimumLevel = ParseLevel(value) };
                displayed = null;
                return $"Minimum log level: {logFilter.MinimumLevel?.ToString() ?? "all"}.";
            case "source":
                logFilter = logFilter with { Source = EmptyToNull(value) };
                displayed = null;
                return $"Log source filter: {logFilter.Source ?? "all"}.";
            case "find":
                logFilter = logFilter with { Text = EmptyToNull(value) };
                displayed = null;
                return $"Log text filter: {logFilter.Text ?? "all"}.";
            case "clear":
                logFilter = new OperationsLogFilter(Limit: logFilter.Limit);
                displayed = null;
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

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
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
        text.AppendLine($"PLAYERS {server.OnlinePlayers}/{server.MaximumPlayers}  TPS={server.TicksPerSecond:F1}");
        text.AppendLine($"MEMORY  working={FormatBytes(snapshot.Runtime.Host.WorkingSetBytes)} managed={FormatBytes(snapshot.Runtime.ManagedMemoryBytes)} heap={FormatBytes(snapshot.Runtime.GcHeapBytes)}");
        text.AppendLine($"GC      gen0={snapshot.Runtime.Gen0Collections} gen1={snapshot.Runtime.Gen1Collections} gen2={snapshot.Runtime.Gen2Collections} fragmented={FormatBytes(snapshot.Runtime.GcFragmentedBytes)}");
        text.AppendLine($"NETWORK in={snapshot.Network.InboundPackets:N0}/{FormatBytes(snapshot.Network.InboundBytes)} out={snapshot.Network.OutboundPackets:N0}/{FormatBytes(snapshot.Network.OutboundBytes)}");

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
        foreach (ModuleOperationsSnapshot module in snapshot.Modules)
        {
            text.AppendLine($"  {module.Name,-22} {module.Version,-12} {module.State,-10} {module.StartupDuration.TotalMilliseconds,7:F1} ms deps=[{string.Join(",", module.Dependencies)}]{FormatFailure(module.Failure)}");
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
