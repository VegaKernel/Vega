using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

/// <summary>Bounded NPC spawning through the world-owned native adapter.</summary>
public sealed class SpawnMobCommand(IPlayerDirectory players, IWorldNpcCatalog catalog, IWorldNpcService npcs, bool boss = false) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = boss
        ? new("spawnboss", ["sb"], ["vega.world.spawnboss"], AllowServer: false, Syntax: "spawnboss <boss-id|name> [count:1..5]")
        : new("spawnmob", ["sm"], ["vega.world.spawnmob"], AllowServer: false, Syntax: "spawnmob <npc-id|name> [count:1..20]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        int count = 1;
        if (invocation.Arguments.Count is < 1 or > 2 || invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > (boss ? 5 : 20)))
            return new(CommandExecutionStatus.InvalidUsage, "spawnmob.usage");
        if (invocation.Principal is not IPlayerCommandPrincipal { SessionId: { } session } player ||
            !players.TryGet(player.PlayerId, out var actor) || actor?.SessionId != session || actor.WorldId != invocation.WorldId || actor.IsDead)
            return CommandExecutionResult.Failed("spawnmob.player_unavailable");
        string query = boss ? BossAlias(invocation.Arguments[0]) : invocation.Arguments[0];
        var matches = await catalog.FindTypesAsync(query, token).ConfigureAwait(false);
        if (boss) matches = matches.Where(n => n.Boss).ToArray();
        if (matches.Count != 1) return NpcCommandResults.Ambiguous(matches);
        var type = matches[0];
        int spawned = 0;
        for (; spawned < count; spawned++)
        {
            token.ThrowIfCancellationRequested();
            if (!players.TryGet(player.PlayerId, out var current) || current?.SessionId != session || current.WorldId != invocation.WorldId || current.IsDead) break;
            var result = await npcs.SpawnAsync(new(invocation.WorldId, type.Type, current.Position, invocation.Principal.Name, current.Id.Slot)
                { TargetPlayer = current, Boss = boss }, token)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                if (spawned == 0) return CommandExecutionResult.Failed(result.ErrorCode ?? "spawnmob.failed");
                break;
            }
        }
        return new CommandExecutionResult(spawned == count ? CommandExecutionStatus.Success : CommandExecutionStatus.Failed,
            spawned == count ? null : "spawnmob.partial", [MessageDescriptor.Info("spawnmob.result", spawned, count, type.Name)
                .WithDefaultText("Spawned {0}/{1} {2}.")]);
    }

    private static string BossAlias(string value) => value.ToLowerInvariant() switch
    {
        "eye" or "eoc" => "4",
        "eater" or "eow" => "13",
        "brain" or "boc" => "266",
        "king" or "ks" => "50",
        "qb" => "222",
        "wof" => "113",
        "prime" => "127",
        "duke" or "fishron" => "370",
        "moon" or "ml" or "moon lord" => "398",
        "empress" or "eol" => "636",
        "qs" => "657",
        "cultist" or "lunatic" or "lc" => "439",
        _ => value
    };
}

/// <summary>Native NPC combat (unlike clear, this can produce normal loot).</summary>
public sealed class ButcherCommand(IWorldNpcCatalog catalog, IWorldNpcService npcs) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("butcher", [], ["vega.world.butcher"], Syntax: "butcher [npc-id|name]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count > 1) return new(CommandExecutionStatus.InvalidUsage, "butcher.usage");
        NpcTypeDefinition? type = null;
        if (invocation.Arguments.Count == 1)
        {
            var matches = await catalog.FindTypesAsync(invocation.Arguments[0], token).ConfigureAwait(false);
            if (matches.Count != 1) return NpcCommandResults.Ambiguous(matches);
            type = matches[0];
        }
        bool includeBosses = type is not null && invocation.Principal.HasPermission("vega.world.butcher.boss");
        if (type?.Boss == true && !includeBosses) return new(CommandExecutionStatus.Forbidden, "butcher.boss_permission");
        var result = await npcs.ButcherAsync(new(invocation.WorldId, type?.Type, includeBosses, invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("butcher.completed", result.Affected)
            .WithDefaultText("Defeated {0} NPCs.")) : CommandExecutionResult.Failed(result.ErrorCode ?? "butcher.failed");
    }
}

public sealed class RenameNpcCommand(IWorldNpcCatalog catalog, IWorldNpcService npcs) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("renamenpc", [], ["vega.world.renamenpc"], Syntax: "renamenpc <npc-id|name> <new name>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 2 || !NpcCommandResults.ValidName(invocation.Arguments[1]))
            return new(CommandExecutionStatus.InvalidUsage, "renamenpc.usage");
        var matches = await catalog.FindTypesAsync(invocation.Arguments[0], token).ConfigureAwait(false);
        if (matches.Count != 1) return NpcCommandResults.Ambiguous(matches);
        var result = await npcs.RenameAsync(new(invocation.WorldId, matches[0].Type, invocation.Arguments[1], invocation.Principal.Name), token).ConfigureAwait(false);
        return result.IsSuccess ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("renamenpc.completed", result.Affected, invocation.Arguments[1])
            .WithDefaultText("Renamed {0} town NPCs to {1}.")) : CommandExecutionResult.Failed(result.ErrorCode ?? "renamenpc.failed");
    }
}

internal static class NpcCommandResults
{
    public static bool ValidName(string name) => name.Length is > 0 and <= 200 && !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl);

    public static CommandExecutionResult Ambiguous(IReadOnlyList<NpcTypeDefinition> matches) => CommandExecutionResult.Failed("spawnmob.ambiguous",
        MessageDescriptor.Error("spawnmob.ambiguous", string.Join(", ", matches.Take(12).Select(n => $"{n.Name} ({n.Type})")))
            .WithDefaultText("NPC type not found or ambiguous. Matches: {0}."));
}
