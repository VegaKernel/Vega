using System.Runtime.CompilerServices;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

/// <summary>Curated access to the world hosted by the current Vega process.</summary>
public interface IPluginWorld
{
    WorldId Current { get; }
    IPluginWorldEnvironment Environment { get; }
    IPluginWorldTiles Tiles { get; }
    IPluginWorldChests Chests { get; }
    IPluginWorldNpcs Npcs { get; }
    IPluginWorldScenes Scenes { get; }
    IPluginFakeProvider FakeProvider => throw new NotSupportedException("FakeProvider is unavailable in this host.");
}

public interface IPluginWorldEnvironment
{
    ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(CancellationToken cancellationToken = default);

    ValueTask<WorldEnvironmentMutationResult> PatchAsync(
        WorldEnvironmentPatch patch,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldTiles
{
    ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
        SceneBounds bounds,
        CancellationToken cancellationToken = default);

    ValueTask<WorldTileMutationResult> ReplaceAsync(
        WorldTileAreaSnapshot expected,
        SceneTileArea replacement,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldChests
{
    ValueTask<ChestSnapshot?> CaptureAsync(
        int x,
        int y,
        CancellationToken cancellationToken = default);

    ValueTask<ChestMutationResult> ReplaceAsync(
        ChestSnapshot expected,
        IReadOnlyList<ChestItemSnapshot> items,
        string? name = null,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldNpcs
{
    ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(CancellationToken cancellationToken = default);

    ValueTask<NpcSnapshot?> CaptureAsync(
        NpcHandle handle,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> SpawnAsync(
        int type,
        PlayerPosition position,
        int? targetPlayerSlot = null,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> TeleportAsync(
        NpcHandle expected,
        PlayerPosition position,
        CancellationToken cancellationToken = default);

    ValueTask<NpcOperationResult> DespawnAsync(
        NpcHandle expected,
        CancellationToken cancellationToken = default);
}

public interface IPluginWorldScenes
{
    /// <summary>Creates a plugin-owned scene which is automatically detached on plugin unload.</summary>
    ISceneLease Create(string name);
}

public interface IPluginPlayerInventory
{
    ValueTask<PlayerInventorySnapshot?> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default);

    ValueTask<InventoryMutationResult> ReplaceSlotAsync(
        PlayerInventorySnapshot expected,
        InventorySlotReplacement replacement,
        CancellationToken cancellationToken = default);
}

internal sealed record VegaPluginRuntimeServices(
    WorldId WorldId,
    IWorldEnvironmentService? Environment,
    IWorldTileService? Tiles,
    IChestService? Chests,
    IWorldNpcService? Npcs,
    ISceneRuntime? Scenes,
    IPlayerInventoryService? Inventory,
    ISceneVisibilityRuntime? Visibility = null)
{
    public static VegaPluginRuntimeServices Empty { get; } = new(
        WorldId.Primary,
        null,
        null,
        null,
        null,
        null,
        null);
}

internal static partial class VegaPluginRuntime
{
    private static readonly ConditionalWeakTable<IVegaPluginContext, PluginWorld> Worlds = new();
    private static VegaPluginRuntimeServices services = VegaPluginRuntimeServices.Empty;
    private static readonly IPluginPlayerInventory InventoryFacade = new PluginPlayerInventory();

    internal static IPluginWorld World(IVegaPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Worlds.GetValue(context, static value => new PluginWorld(value));
    }

    internal static IPluginPlayerInventory Inventory => InventoryFacade;

    internal static void Configure(VegaPluginRuntimeServices value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref services, value);
    }

    private static VegaPluginRuntimeServices Services => Volatile.Read(ref services);

    private static string Actor(IVegaPluginContext context)
    {
        string directory = context.Configuration.Directory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(directory);
        return string.IsNullOrWhiteSpace(name) ? "plugin" : name;
    }

    private sealed class PluginWorld(IVegaPluginContext context) : IPluginWorld
    {
        public WorldId Current => Services.WorldId;
        public IPluginWorldEnvironment Environment { get; } = new PluginEnvironment(context);
        public IPluginWorldTiles Tiles { get; } = new PluginTiles(context);
        public IPluginWorldChests Chests { get; } = new PluginChests(context);
        public IPluginWorldNpcs Npcs { get; } = new PluginNpcs(context);
        public IPluginWorldScenes Scenes { get; } = new PluginScenes(context);
        public IPluginFakeProvider FakeProvider { get; } = new PluginFakeProvider(context);
    }

    private sealed class PluginEnvironment(IVegaPluginContext context) : IPluginWorldEnvironment
    {
        public ValueTask<WorldEnvironmentSnapshot?> CaptureAsync(CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Environment?.CaptureAsync(current.WorldId, cancellationToken) ??
                ValueTask.FromResult<WorldEnvironmentSnapshot?>(null);
        }

        public ValueTask<WorldEnvironmentMutationResult> PatchAsync(
            WorldEnvironmentPatch patch,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(patch);
            VegaPluginRuntimeServices current = Services;
            return current.Environment?.PatchAsync(
                new WorldEnvironmentMutationRequest(current.WorldId, patch, Actor(context)),
                cancellationToken) ??
                ValueTask.FromResult(new WorldEnvironmentMutationResult(
                    WorldEnvironmentMutationStatus.DispatcherUnavailable,
                    ErrorCode: "world.environment.unavailable"));
        }
    }

    private sealed class PluginTiles(IVegaPluginContext context) : IPluginWorldTiles
    {
        public ValueTask<WorldTileAreaSnapshot?> CaptureAsync(
            SceneBounds bounds,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Tiles?.CaptureAsync(current.WorldId, bounds, cancellationToken) ??
                ValueTask.FromResult<WorldTileAreaSnapshot?>(null);
        }

        public ValueTask<WorldTileMutationResult> ReplaceAsync(
            WorldTileAreaSnapshot expected,
            SceneTileArea replacement,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(replacement);
            VegaPluginRuntimeServices current = Services;
            if (expected.WorldId != current.WorldId)
            {
                return ValueTask.FromResult(new WorldTileMutationResult(
                    WorldTileMutationStatus.Invalid,
                    ErrorCode: "world.tiles.wrong_world"));
            }

            return current.Tiles?.ReplaceAsync(
                new WorldTileMutationRequest(
                    current.WorldId,
                    expected.Bounds,
                    expected.Revision,
                    replacement,
                    Actor(context)),
                cancellationToken) ??
                ValueTask.FromResult(new WorldTileMutationResult(
                    WorldTileMutationStatus.Failed,
                    ErrorCode: "world.tiles.unavailable"));
        }
    }

    private sealed class PluginChests(IVegaPluginContext context) : IPluginWorldChests
    {
        public ValueTask<ChestSnapshot?> CaptureAsync(
            int x,
            int y,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Chests?.CaptureAsync(
                new ChestIdentity(current.WorldId, x, y),
                cancellationToken) ?? ValueTask.FromResult<ChestSnapshot?>(null);
        }

        public ValueTask<ChestMutationResult> ReplaceAsync(
            ChestSnapshot expected,
            IReadOnlyList<ChestItemSnapshot> items,
            string? name = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            ArgumentNullException.ThrowIfNull(items);
            VegaPluginRuntimeServices current = Services;
            if (expected.Identity.WorldId != current.WorldId)
            {
                return ValueTask.FromResult(new ChestMutationResult(
                    ChestMutationStatus.Invalid,
                    ErrorCode: "chest.wrong_world"));
            }

            return current.Chests?.ReplaceAsync(
                new ChestMutationRequest(
                    expected.Identity,
                    expected.Revision,
                    items,
                    Actor(context),
                    Name: name),
                cancellationToken) ??
                ValueTask.FromResult(new ChestMutationResult(
                    ChestMutationStatus.Failed,
                    ErrorCode: "chest.unavailable"));
        }
    }

    private sealed class PluginNpcs(IVegaPluginContext context) : IPluginWorldNpcs
    {
        public ValueTask<IReadOnlyList<NpcSnapshot>> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Npcs?.SnapshotAsync(current.WorldId, cancellationToken) ??
                ValueTask.FromResult<IReadOnlyList<NpcSnapshot>>([]);
        }

        public ValueTask<NpcSnapshot?> CaptureAsync(
            NpcHandle handle,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (handle.WorldId != current.WorldId)
            {
                return ValueTask.FromResult<NpcSnapshot?>(null);
            }

            return current.Npcs?.CaptureAsync(handle, cancellationToken) ??
                ValueTask.FromResult<NpcSnapshot?>(null);
        }

        public ValueTask<NpcOperationResult> SpawnAsync(
            int type,
            PlayerPosition position,
            int? targetPlayerSlot = null,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Npcs?.SpawnAsync(
                new NpcSpawnRequest(current.WorldId, type, position, Actor(context), targetPlayerSlot),
                cancellationToken) ?? Unavailable();
        }

        public ValueTask<NpcOperationResult> TeleportAsync(
            NpcHandle expected,
            PlayerPosition position,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (expected.WorldId != current.WorldId)
            {
                return ValueTask.FromResult(new NpcOperationResult(
                    NpcOperationStatus.Invalid,
                    ErrorCode: "npc.wrong_world"));
            }

            return current.Npcs?.TeleportAsync(
                new NpcTeleportRequest(expected, position, Actor(context)),
                cancellationToken) ?? Unavailable();
        }

        public ValueTask<NpcOperationResult> DespawnAsync(
            NpcHandle expected,
            CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            if (expected.WorldId != current.WorldId)
            {
                return ValueTask.FromResult(new NpcOperationResult(
                    NpcOperationStatus.Invalid,
                    ErrorCode: "npc.wrong_world"));
            }

            return current.Npcs?.DespawnAsync(
                new NpcDespawnRequest(expected, Actor(context)),
                cancellationToken) ?? Unavailable();
        }

        private static ValueTask<NpcOperationResult> Unavailable() => ValueTask.FromResult(
            new NpcOperationResult(
                NpcOperationStatus.DispatcherUnavailable,
                ErrorCode: "npc.unavailable"));
    }

    private sealed class PluginScenes(IVegaPluginContext context) : IPluginWorldScenes
    {
        public ISceneLease Create(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            VegaPluginRuntimeServices current = Services;
            if (current.Scenes is null)
            {
                throw new InvalidOperationException("Scene capability is unavailable in this Vega host.");
            }

            ISceneLease lease = current.Scenes.CreateScene(current.WorldId, name, Actor(context));
            if (context is IHotModuleContext owner)
            {
                try { return owner.Track(lease); }
                catch { lease.Dispose(); throw; }
            }

            lease.Dispose();
            throw new InvalidOperationException("Plugin host does not expose a tracked hot-module lifetime.");
        }
    }

    private sealed class PluginPlayerInventory : IPluginPlayerInventory
    {
        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
            PlayerId playerId,
            CancellationToken cancellationToken = default) =>
            Services.Inventory?.CaptureAsync(playerId, cancellationToken) ??
            ValueTask.FromResult<PlayerInventorySnapshot?>(null);

        public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
            PlayerInventorySnapshot expected,
            InventorySlotReplacement replacement,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(expected);
            return Services.Inventory?.ReplaceSlotAsync(
                expected.PlayerId,
                expected.Revision,
                replacement,
                cancellationToken) ??
                ValueTask.FromResult(new InventoryMutationResult(
                    InventoryMutationStatus.DispatcherUnavailable,
                    ErrorCode: "inventory.unavailable"));
        }
    }
}
