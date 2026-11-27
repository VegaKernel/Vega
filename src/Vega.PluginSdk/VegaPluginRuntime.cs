using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

internal sealed record VegaPluginRuntimeServices(
    WorldId WorldId,
    IWorldEnvironmentService? Environment,
    IWorldTileService? Tiles,
    IChestService? Chests,
    IWorldNpcService? Npcs,
    ISceneRuntime? Scenes,
    IPlayerInventoryService? Inventory,
    ISceneVisibilityRuntime? Visibility = null,
    IWorldSchematicService? Schematics = null,
    IWorldNpcActorService? NpcActors = null,
    IRegionService? Regions = null)
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
        public IPluginWorldSchematics Schematics { get; } = new PluginSchematics();
        public IPluginFakeProvider FakeProvider { get; } = new PluginFakeProvider(context);
        public IPluginRegions Regions { get; } = new PluginRegions(context);
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

        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(PlayerSnapshot player, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(player);
            var current = Services;
            return player.SessionId is not null && player.WorldId == current.WorldId && current.Inventory is not null
                ? current.Inventory.CaptureAsync(player, cancellationToken) : ValueTask.FromResult<PlayerInventorySnapshot?>(null);
        }

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
