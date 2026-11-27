using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Localization;
using Vega.Contracts.Players;
using Vega.Core.Players;

namespace Vega.Core.Commands.Players;

public sealed class PlaytimeCommand(IPlayerDirectory players, PlayerPlaytimeService playtime) : ICommandHandler
{
    private const string Source = "vega.playtime";
    public CommandDescriptor Descriptor { get; } = new("playtime", [], [], AllowServer: false, Syntax: "playtime");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal principal || invocation.Arguments.Count != 0)
            return PlayerAccountCommandResult.Usage("playtime");
        if (principal.SessionId is not { } session || !players.TryGet(principal.PlayerId, out var player) ||
            player is null || player.SessionId != session || player.WorldId != invocation.WorldId)
            return CommandExecutionResult.Failed("playtime.session-unavailable",
                MessageDescriptor.Warning("playtime.session-unavailable").WithSource(Source).WithDefaultText("The current game session is unavailable."));
        var result = await playtime.ReadCurrentAsync(player, cancellationToken).ConfigureAwait(false);
        if (result is not { } time)
            return CommandExecutionResult.Failed("playtime.not-authenticated-in-world",
                MessageDescriptor.Warning("playtime.not-authenticated-in-world").WithSource(Source).WithDefaultText("Enter the world and log in to view play time."));
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("playtime.current", Format(time.Current)).WithSource(Source).WithDefaultText("Current authenticated world session: {0}"),
            MessageDescriptor.Info("playtime.world", Format(time.Total.InWorld), time.Total.SessionsInWorld).WithSource(Source).WithDefaultText("Saved time in this world: {0} ({1} sessions)"),
            MessageDescriptor.Info("playtime.total", Format(time.Total.Total), time.Total.Sessions).WithSource(Source).WithDefaultText("Saved time across worlds: {0} ({1} sessions; includes the latest saved checkpoint of this session)"));
    }

    private static string Format(TimeSpan duration) => $"{(long)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";

    public static IDisposable RegisterRussianCatalog(ILocalizationService localization) => localization.RegisterCatalog(Source, "ru-RU",
        new Dictionary<string, string>
        {
            ["playtime.session-unavailable"] = "Текущая игровая сессия недоступна.",
            ["playtime.not-authenticated-in-world"] = "Войдите в мир и авторизуйтесь, чтобы узнать игровое время.",
            ["playtime.current"] = "Текущая авторизованная сессия в мире: {0}",
            ["playtime.world"] = "Сохранённое время в этом мире: {0} (сессий: {1})",
            ["playtime.total"] = "Сохранённое время во всех мирах: {0} (сессий: {1}; включает последнее сохранение текущей сессии)"
        });
}
