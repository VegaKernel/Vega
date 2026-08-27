using System.Globalization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;
using Vega.Terraria.Worlds;

namespace Vega.Terraria.Commands;

internal static class TerrariaGameplayUtilityCommandRegistration
{
    public static IDisposable Register(
        ICommandDispatcher dispatcher,
        WorldId runtimeWorldId,
        IGameThreadDispatcher gameThread,
        IPlayerDirectory players,
        IPlayerOperations playerOperations,
        IPlayerMessenger messenger)
    {
        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must expose ICommandRegistry for Terraria gameplay utility commands.");
        }

        var npcs = new TerrariaWorldNpcService(gameThread, runtimeWorldId);
        var environment = new TerrariaWorldEnvironmentService(gameThread, runtimeWorldId);
        var recall = new TerrariaPlayerRecallService(gameThread);
        var wallOfFlesh = new TerrariaWallOfFleshSpawner(gameThread, runtimeWorldId);
        IDisposable[] registrations =
        [
            registry.Register(new HomeCommand(players, recall)),
            registry.Register(new TeleportNpcCommand(players, playerOperations, npcs)),
            registry.Register(new SpawnBossCommand(players, npcs, environment, wallOfFlesh, messenger))
        ];
        return new RegistrationSet(registrations);
    }

    private sealed class RegistrationSet(IDisposable[] registrations) : IDisposable
    {
        private IDisposable[]? owned = registrations;

        public void Dispose()
        {
            IDisposable[]? snapshot = Interlocked.Exchange(ref owned, null);
            if (snapshot is null)
            {
                return;
            }

            for (int index = snapshot.Length - 1; index >= 0; index--)
            {
                snapshot[index].Dispose();
            }
        }
    }
}

internal sealed class HomeCommand(
    IPlayerDirectory players,
    IPlayerRecallOperations recall) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "home",
        [],
        ["vega.players.home"],
        AllowServer: false,
        Syntax: "home");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? player) ||
            player is null)
        {
            return Usage("Usage: home.");
        }

        PlayerOperationResult result = await recall
            .RecallHomeAsync(player.Id, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.home.applied")
                    .WithDefaultText("Teleported to your personal spawn point."))
            : Failure(
                result.ErrorCode ?? "command.home.failed",
                result.ErrorCode == "player.recall-home.dead"
                    ? "Dead players cannot go home."
                    : "Could not recall you to your personal spawn point.");
    }

    private static CommandExecutionResult Usage(string text) => new(
        CommandExecutionStatus.InvalidUsage,
        "command.home.usage",
        [MessageDescriptor.Error("command.home.usage").WithDefaultText(text)]);

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

internal sealed class TeleportNpcCommand(
    IPlayerDirectory players,
    IPlayerOperations operations,
    IWorldNpcService npcs) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "tpnpc",
        [],
        ["vega.players.teleport.npc"],
        AllowServer: false,
        Syntax: "tpnpc <npc name>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0 ||
            invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? caller) ||
            caller is null)
        {
            return Usage();
        }

        IReadOnlyList<NpcSnapshot> snapshot = await npcs
            .SnapshotAsync(invocation.WorldId, cancellationToken)
            .ConfigureAwait(false);
        string search = string.Join(" ", invocation.Arguments).Trim();
        NpcSnapshot? target = Resolve(snapshot, search, out NpcSnapshot[] ambiguous);
        if (target is null)
        {
            if (ambiguous.Length > 1)
            {
                return Failure(
                    "command.tpnpc.ambiguous",
                    $"NPC name is ambiguous: {string.Join(", ", ambiguous.Take(8).Select(npc => $"{npc.Name}(#{npc.Handle.Slot})"))}.");
            }

            return Failure("command.tpnpc.not-found", $"No active NPC matches '{search}'.");
        }

        PlayerOperationResult result = await operations
            .TeleportAsync(caller.Id, target.Position, cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.tpnpc.applied", target.Name)
                    .WithDefaultText("Teleported to {0}."))
            : Failure(result.ErrorCode ?? "command.tpnpc.failed", "Could not teleport to the NPC.");
    }

    private static NpcSnapshot? Resolve(
        IReadOnlyList<NpcSnapshot> snapshot,
        string search,
        out NpcSnapshot[] ambiguous)
    {
        ambiguous = [];
        NpcSnapshot? exact = snapshot.FirstOrDefault(npc =>
            npc.Name.Equals(search, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        NpcSnapshot[] matches = snapshot
            .Where(npc => npc.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .Take(9)
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        ambiguous = matches;
        return null;
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.tpnpc.usage",
        [MessageDescriptor.Error("command.tpnpc.usage").WithDefaultText("Usage: tpnpc <npc name>.")]);

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

internal sealed class SpawnBossCommand(
    IPlayerDirectory players,
    IWorldNpcService npcs,
    IWorldEnvironmentService environment,
    IWallOfFleshSpawner wallOfFlesh,
    IPlayerMessenger messenger) : ICommandHandler
{
    private sealed record BossDefinition(string DisplayName, int[] Types, bool ForceNight = false, bool WallOfFlesh = false);

    private static readonly IReadOnlyDictionary<string, BossDefinition> Definitions = CreateDefinitions();

    public CommandDescriptor Descriptor { get; } = new(
        "spawnboss",
        ["sb"],
        ["vega.npc.spawnboss"],
        AllowServer: false,
        Syntax: "spawnboss <boss> [amount:1..50]");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count is < 1 or > 2 ||
            invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? caller) ||
            caller is null)
        {
            return Usage();
        }

        int amount = 1;
        if (invocation.Arguments.Count == 2 &&
            (!int.TryParse(invocation.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount) ||
             amount is < 1 or > 50))
        {
            return Usage();
        }

        string key = invocation.Arguments[0].Trim().ToLowerInvariant();
        if (!Definitions.TryGetValue(key, out BossDefinition? definition))
        {
            return Failure(
                "command.spawnboss.invalid-boss",
                "Unknown boss. Use a quoted full boss name or a familiar alias such as eoc, boc, eow, prime, twins, ml, eol, qs, lc, duke, deerclops or all.");
        }

        if (definition.WallOfFlesh)
        {
            if (amount != 1)
            {
                return Failure("command.spawnboss.wof-amount", "Wall of Flesh can only be spawned once at a time.");
            }

            PlayerOperationResult wof = await wallOfFlesh
                .SpawnAsync(invocation.WorldId, caller.Position, cancellationToken)
                .ConfigureAwait(false);
            if (!wof.IsSuccess)
            {
                return Failure(wof.ErrorCode ?? "command.spawnboss.wof-failed", WallOfFleshFailure(wof.ErrorCode));
            }

            return await CompleteAsync(
                invocation,
                definition.DisplayName,
                amount,
                spawnedEntities: 1,
                cancellationToken).ConfigureAwait(false);
        }

        if (definition.ForceNight)
        {
            WorldEnvironmentMutationResult night = await environment
                .PatchAsync(
                    new WorldEnvironmentMutationRequest(
                        invocation.WorldId,
                        new WorldEnvironmentPatch(IsDaytime: false, TimeTicks: 0),
                        invocation.Principal.Name),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!night.IsSuccess)
            {
                return Failure(night.ErrorCode ?? "command.spawnboss.time-failed", "Could not switch the world to night before spawning this boss.");
            }
        }

        int spawned = 0;
        foreach (int type in definition.Types)
        {
            for (int index = 0; index < amount; index++)
            {
                NpcOperationResult result = await npcs
                    .SpawnAsync(
                        new NpcSpawnRequest(
                            invocation.WorldId,
                            type,
                            caller.Position,
                            invocation.Principal.Name,
                            caller.Id.Slot),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    return Failure(
                        result.ErrorCode ?? "command.spawnboss.spawn-failed",
                        $"Spawned {spawned} boss entity/entities before the operation failed.");
                }
                spawned++;
            }
        }

        return await CompleteAsync(
            invocation,
            definition.DisplayName,
            amount,
            spawned,
            cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyDictionary<string, BossDefinition> CreateDefinitions()
    {
        var map = new Dictionary<string, BossDefinition>(StringComparer.OrdinalIgnoreCase);
        static void Add(Dictionary<string, BossDefinition> target, BossDefinition definition, params string[] aliases)
        {
            foreach (string alias in aliases)
            {
                target[alias] = definition;
            }
        }

        Add(map, new BossDefinition("all bosses", [4, 13, 35, 50, 125, 126, 127, 134, 222, 245, 262, 266, 370, 398, 439, 636, 657], ForceNight: true), "*", "all");
        Add(map, new BossDefinition("Brain of Cthulhu", [266]), "brain", "brain of cthulhu", "boc");
        Add(map, new BossDefinition("Destroyer", [134], true), "destroyer");
        Add(map, new BossDefinition("Duke Fishron", [370]), "duke", "duke fishron", "fishron");
        Add(map, new BossDefinition("Eater of Worlds", [13]), "eater", "eater of worlds", "eow");
        Add(map, new BossDefinition("Eye of Cthulhu", [4], true), "eye", "eye of cthulhu", "eoc");
        Add(map, new BossDefinition("Golem", [245]), "golem");
        Add(map, new BossDefinition("King Slime", [50]), "king", "king slime", "ks");
        Add(map, new BossDefinition("Plantera", [262]), "plantera");
        Add(map, new BossDefinition("Skeletron Prime", [127], true), "prime", "skeletron prime");
        Add(map, new BossDefinition("Queen Bee", [222]), "queen bee", "qb");
        Add(map, new BossDefinition("Skeletron", [35], true), "skeletron");
        Add(map, new BossDefinition("Twins", [125, 126], true), "twins");
        Add(map, new BossDefinition("Wall of Flesh", [], WallOfFlesh: true), "wof", "wall of flesh");
        Add(map, new BossDefinition("Moon Lord", [398]), "moon", "moon lord", "ml");
        Add(map, new BossDefinition("Empress of Light", [636]), "empress", "empress of light", "eol");
        Add(map, new BossDefinition("Queen Slime", [657]), "queen slime", "qs");
        Add(map, new BossDefinition("Lunatic Cultist", [439]), "lunatic", "lunatic cultist", "cultist", "lc");
        Add(map, new BossDefinition("Betsy", [551]), "betsy");
        Add(map, new BossDefinition("Flying Dutchman", [491]), "flying dutchman", "flying", "dutchman");
        Add(map, new BossDefinition("Mourning Wood", [325]), "mourning wood");
        Add(map, new BossDefinition("Pumpking", [327]), "pumpking");
        Add(map, new BossDefinition("Everscream", [344]), "everscream");
        Add(map, new BossDefinition("Santa-NK1", [346]), "santa-nk1", "santa");
        Add(map, new BossDefinition("Ice Queen", [345]), "ice queen");
        Add(map, new BossDefinition("Martian Saucer", [392]), "martian saucer");
        Add(map, new BossDefinition("Solar Pillar", [517]), "solar pillar");
        Add(map, new BossDefinition("Nebula Pillar", [507]), "nebula pillar");
        Add(map, new BossDefinition("Vortex Pillar", [422]), "vortex pillar");
        Add(map, new BossDefinition("Stardust Pillar", [493]), "stardust pillar");
        Add(map, new BossDefinition("Deerclops", [668]), "deerclops");
        return map;
    }

    private static string WallOfFleshFailure(string? errorCode) => errorCode switch
    {
        "boss.wof.already-active" => "There is already a Wall of Flesh.",
        "boss.wof.not-in-hell" => "Wall of Flesh must be spawned in the underworld.",
        _ => "Could not spawn the Wall of Flesh."
    };

    private async ValueTask<CommandExecutionResult> CompleteAsync(
        CommandInvocation invocation,
        string displayName,
        int amount,
        int spawnedEntities,
        CancellationToken cancellationToken)
    {
        string action = amount == 1
            ? $"spawned {displayName} once ({spawnedEntities} boss entity/entities)."
            : $"spawned {displayName} {amount} times ({spawnedEntities} boss entity/entities).";
        string message = $"{invocation.Principal.Name} {action}";
        if (!invocation.Silent)
        {
            await NpcCommandBroadcast.SendToOthersAsync(
                players,
                messenger,
                invocation,
                message,
                cancellationToken).ConfigureAwait(false);
        }

        return Success(invocation.Silent ? $"You {action}" : message);
    }

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.spawnboss.usage",
        [MessageDescriptor.Error("command.spawnboss.usage").WithDefaultText("Usage: spawnboss <boss> [amount:1..50]. Quote multi-word boss names.")]);

    private static CommandExecutionResult Success(string text) =>
        CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.spawnboss.applied").WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

internal interface IWallOfFleshSpawner
{
    ValueTask<PlayerOperationResult> SpawnAsync(
        WorldId worldId,
        PlayerPosition position,
        CancellationToken cancellationToken);
}

internal sealed class TerrariaWallOfFleshSpawner(
    IGameThreadDispatcher dispatcher,
    WorldId runtimeWorldId) : IWallOfFleshSpawner
{
    public ValueTask<PlayerOperationResult> SpawnAsync(
        WorldId worldId,
        PlayerPosition position,
        CancellationToken cancellationToken)
    {
        if (worldId != runtimeWorldId)
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed("boss.wof.wrong-world"));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed("boss.wof.cancelled"));
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed("boss.wof.dispatcher-unavailable"));
        }

        PlayerOperationResult Spawn()
        {
            dispatcher.AssertGameThread();
            if (global::Terraria.Main.wofNPCIndex != -1)
            {
                return PlayerOperationResult.Failed("boss.wof.already-active");
            }

            if (position.Y / 16f < global::Terraria.Main.maxTilesY - 205)
            {
                return PlayerOperationResult.Failed("boss.wof.not-in-hell");
            }

            global::Terraria.NPC.SpawnWOF(new Microsoft.Xna.Framework.Vector2(position.X, position.Y));
            return PlayerOperationResult.Applied();
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(Spawn());
        }

        var completion = new TaskCompletionSource<PlayerOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("boss.wof.spawn", () =>
            {
                try
                {
                    completion.TrySetResult(Spawn());
                }
                catch
                {
                    completion.TrySetResult(PlayerOperationResult.Failed("boss.wof.spawn-failed"));
                }
            }))
        {
            return ValueTask.FromResult(PlayerOperationResult.Failed("boss.wof.dispatcher-unavailable"));
        }

        return new ValueTask<PlayerOperationResult>(completion.Task.WaitAsync(cancellationToken));
    }
}
