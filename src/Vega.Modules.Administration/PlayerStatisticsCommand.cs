using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Modules.Administration;

/// <summary>Native character counters, scoped to connected players in this world.</summary>
public sealed class PlayerDeathStatisticsCommand(IPlayerDirectory players, bool pvp, bool all) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = all
        ? new(pvp ? "allpvpdeath" : "alldeath", [], ["vega.players.deaths.others"], Syntax: pvp ? "allpvpdeath [page]" : "alldeath [page]")
        : new(pvp ? "pvpdeath" : "death", [], [], AllowServer: false, Syntax: pvp ? "pvpdeath" : "death");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (!all)
        {
            if (invocation.Arguments.Count != 0) return ValueTask.FromResult(Usage());
            var player = VitalTargets.Self(players, invocation);
            return ValueTask.FromResult(player is null ? CommandExecutionResult.Failed("death.player_unavailable") :
                CommandExecutionResult.WithMessages(Row(player)));
        }

        int page = 1;
        if (invocation.Arguments.Count > 1 || invocation.Arguments.Count == 1 &&
            (!int.TryParse(invocation.Arguments[0], NumberStyles.None, CultureInfo.InvariantCulture, out page) || page < 1))
            return ValueTask.FromResult(Usage());
        const int pageSize = 8;
        var online = players.SnapshotOnlinePlayers().Where(p => p.WorldId == invocation.WorldId && p.SessionId.HasValue)
            .OrderByDescending(Count).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        int pages = Math.Max(1, (online.Length + pageSize - 1) / pageSize);
        if (page > pages) return ValueTask.FromResult(Usage());
        List<MessageDescriptor> messages = [MessageDescriptor.Info("death.page", pvp ? "PvP" : "PvE", page, pages)
            .WithDefaultText("{0} deaths of online players ({1}/{2}):")];
        messages.AddRange(online.Skip((page - 1) * pageSize).Take(pageSize).Select(Row));
        return ValueTask.FromResult(CommandExecutionResult.WithMessages([.. messages]));
    }

    private int Count(PlayerSnapshot player) => Math.Max(0, pvp ? player.DeathsPvp : player.DeathsPve);
    private MessageDescriptor Row(PlayerSnapshot player) => MessageDescriptor.Info("death.count", player.Name, pvp ? "PvP" : "PvE", Count(player))
        .WithDefaultText("{0}: {1} deaths = {2}.");
    private static CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "death.usage");
}
