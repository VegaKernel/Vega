using System.Runtime.CompilerServices;
using System.Collections.Frozen;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.PluginSdk;

internal static partial class VegaPluginRuntime
{
    private sealed class PluginSchematics : IPluginWorldSchematics
    {
        public ValueTask<WorldSchematicCaptureResult> CaptureAsync(SceneBounds bounds, CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Schematics?.CaptureAsync(current.WorldId, bounds, cancellationToken) ??
                ValueTask.FromResult(new WorldSchematicCaptureResult(WorldSchematicCaptureStatus.Unavailable));
        }
        public ValueTask<WorldSchematicPlacementResult> PlaceAsync(ReadOnlyMemory<byte> data, int x, int y, CancellationToken cancellationToken = default)
        {
            VegaPluginRuntimeServices current = Services;
            return current.Schematics?.PlaceAsync(current.WorldId, data, x, y, cancellationToken) ??
                ValueTask.FromResult(new WorldSchematicPlacementResult(WorldSchematicPlacementStatus.Unavailable));
        }
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
                    Actor(context)) { ExpectedTiles = expected.Tiles, ExpectedWorldInstanceId = expected.WorldInstanceId },
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
}
