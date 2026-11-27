using Vega.Contracts.Commands;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

/// <summary>Client-reported platform observations. Neither command establishes a device or operating-system identity.</summary>
public sealed class PlatformCommand(IPlayerDirectory players, bool summary = false) : ICommandHandler
{
    private const string Permission = "vega.players.platform.others";
    private const string Source = "vega.platform";
    public CommandDescriptor Descriptor { get; } = summary
        ? new("platforms", [], [Permission], Syntax: "platforms")
        : new("platform", [], [], Syntax: "platform [player]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (summary)
        {
            if (invocation.Arguments.Count != 0) return ValueTask.FromResult(Usage("platforms"));
            if (!invocation.Principal.HasPermission(Permission)) return ValueTask.FromResult(Denied());
            var counts = PlayerPlatformStatistics.Snapshot(players.SnapshotOnlinePlayers(), invocation.WorldId);
            List<MessageDescriptor> messages = [Info("platform.summary.title", "Client-reported platforms of {0} online players in this world:", counts.Sum(row => row.Count))];
            foreach (var entry in counts)
                messages.Add(entry.Platform switch
                {
                    ClientPlatformKind.Unknown => Info("platform.summary.unknown", "Unknown: {0}", entry.Count),
                    ClientPlatformKind.Unrecognized => Info("platform.summary.unrecognized", "Unrecognized: {0}", entry.Count),
                    _ => Info("platform.summary.reported", "{0}: {1}", Label(entry.Platform), entry.Count)
                });
            return ValueTask.FromResult(CommandExecutionResult.WithMessages([.. messages]));
        }

        if (invocation.Arguments.Count > 1 || invocation.Principal.IsServer && invocation.Arguments.Count == 0)
            return ValueTask.FromResult(Usage("platform [player]"));
        if (invocation.Arguments.Count == 1 && !invocation.Principal.HasPermission(Permission))
            return ValueTask.FromResult(Denied());
        var player = invocation.Arguments.Count == 0 ? VitalTargets.Self(players, invocation) : VitalTargets.Find(players, invocation, invocation.Arguments[0]);
        return ValueTask.FromResult(player is null
            ? CommandExecutionResult.Failed("platform.player_not_found", Error("platform.player_not_found", "No matching current player session in this world."))
            : CommandExecutionResult.WithMessages(DescribePlayerPlatform(player)));
    }

    private static MessageDescriptor DescribePlayerPlatform(PlayerSnapshot player) => player.Platform.Kind switch
    {
        ClientPlatformKind.Unknown => Info("platform.player.unknown", "{0}: client platform is unknown.", player.Name),
        ClientPlatformKind.Unrecognized => Info("platform.player.unrecognized", "{0}: client reported an unrecognized platform.", player.Name),
        _ => Info("platform.player.reported", "{0}: client-reported platform: {1}.", player.Name, Label(player.Platform.Kind))
    };

    internal static string Label(ClientPlatformKind kind) => kind switch
    {
        ClientPlatformKind.Pc => "Steam / PC",
        ClientPlatformKind.Switch => "Nintendo Switch",
        ClientPlatformKind.GameCenter => "Game Center",
        _ => kind.ToString()
    };

    private static MessageDescriptor Info(string key, string text, params object?[] args) =>
        MessageDescriptor.Info(key, args).WithSource(Source).WithDefaultText(text);
    private static MessageDescriptor Error(string key, string text, params object?[] args) =>
        MessageDescriptor.Error(key, args).WithSource(Source).WithDefaultText(text);
    private static CommandExecutionResult Usage(string syntax) => new(CommandExecutionStatus.InvalidUsage, "platform.usage",
        [Error("platform.usage", "Usage: {0}.", syntax)]);
    private static CommandExecutionResult Denied() => new(CommandExecutionStatus.Forbidden, "platform.others_permission",
        [Error("platform.others_permission", "You do not have permission to inspect other players' platforms.")]);

    public static IDisposable RegisterRussianCatalog(ILocalizationService localization) => localization.RegisterCatalog(Source, "ru-RU",
        new Dictionary<string, string>
        {
            ["platform.summary.title"] = "Платформы по данным клиентов ({0} игроков в этом мире):",
            ["platform.summary.unknown"] = "Неизвестно: {0}",
            ["platform.summary.unrecognized"] = "Не распознано: {0}",
            ["platform.summary.reported"] = "{0}: {1}",
            ["platform.player.unknown"] = "{0}: платформа клиента неизвестна.",
            ["platform.player.unrecognized"] = "{0}: клиент сообщил нераспознанную платформу.",
            ["platform.player.reported"] = "{0}: платформа по данным клиента: {1}.",
            ["platform.player_not_found"] = "В текущем мире не найдена подходящая сессия игрока.",
            ["platform.usage"] = "Использование: {0}.",
            ["platform.others_permission"] = "Нет прав на просмотр платформ других игроков."
        });
}
