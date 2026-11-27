using TerraRuntime.Schematics;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Scenes;

/// <summary>Shares the bounded schematic lane with operator commands.</summary>
public sealed class SchematicSnapshotService(WorldId worldId, ISchematicCaptureSource source,
    IGameThreadDispatcher dispatcher, Func<SchematicDocument, SceneTileArea, SceneBounds, WorldSchematicPlacementResult>? place = null) : IWorldSchematicService
{
    public async ValueTask<WorldSchematicCaptureResult> CaptureAsync(WorldId requestedWorld, SceneBounds bounds,
        CancellationToken cancellationToken = default)
    {
        if (requestedWorld != worldId) return new(WorldSchematicCaptureStatus.Invalid, Error: "World is not owned by this process.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!SchematicOperations.Gate.Wait(0)) return new(WorldSchematicCaptureStatus.Busy);
        try
        {
            var document = await CaptureDocumentAsync(new(bounds.X, bounds.Y, bounds.Width, bounds.Height), cancellationToken).ConfigureAwait(false);
            if (document is null) return new(WorldSchematicCaptureStatus.Unavailable);
            byte[] encoded = await Task.Run(() => SchematicBinary.Serialize(document), cancellationToken).ConfigureAwait(false);
            return new(WorldSchematicCaptureStatus.Captured, encoded);
        }
        catch (Exception ex) when (ex is ArgumentException or SchematicFormatException or OverflowException)
        { return new(WorldSchematicCaptureStatus.Invalid, Error: ex.Message); }
        finally { SchematicOperations.Gate.Release(); }
    }

    public async ValueTask<WorldSchematicPlacementResult> PlaceAsync(WorldId requestedWorld, ReadOnlyMemory<byte> data,
        int x, int y, CancellationToken cancellationToken = default)
    {
        if (requestedWorld != worldId)
            return new(WorldSchematicPlacementStatus.Unavailable, Error: "World is not owned by this process.");
        if (data.Length > SchematicLimits.MaxFileBytes) return new(WorldSchematicPlacementStatus.Invalid, Error: "Schematic exceeds its file-size limit.");
        return await PlaceCoreAsync(() => SchematicBinary.Deserialize(data.Span), x, y, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask<WorldSchematicPlacementResult> PlaceFileAsync(string path, int x, int y, CancellationToken cancellationToken) =>
        PlaceCoreAsync(() => SchematicFile.Load(path), x, y, cancellationToken);

    private async ValueTask<WorldSchematicPlacementResult> PlaceCoreAsync(Func<SchematicDocument> load,
        int x, int y, CancellationToken cancellationToken)
    {
        if (worldId == WorldId.Primary || place is null)
            return new(WorldSchematicPlacementStatus.Unavailable, Error: "Native placement is available only in an isolated sandbox.");
        cancellationToken.ThrowIfCancellationRequested();
        if (!SchematicOperations.Gate.Wait(0)) return new(WorldSchematicPlacementStatus.Busy);
        try
        {
            var prepared = await Task.Run(() =>
            {
                // As with other async memory APIs, the caller retains the input until completion.
                var document = load();
                if (document.Tiles.Length > WorldTileOperationLimits.MaximumTiles)
                    throw new SchematicFormatException("Schematic exceeds the native region operation limit.");
                var projection = SchematicSceneProjection.CreateMaterialization(document, x, y);
                return (Document: document, projection.Tiles, projection.Bounds);
            }, cancellationToken).ConfigureAwait(false);
            return await DispatchAsync(() => place(prepared.Document, prepared.Tiles, prepared.Bounds),
                new WorldSchematicPlacementResult(WorldSchematicPlacementStatus.Unavailable), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or SchematicFormatException or OverflowException or IOException or UnauthorizedAccessException)
        { return new(WorldSchematicPlacementStatus.Invalid, Error: ex.Message); }
        finally { SchematicOperations.Gate.Release(); }
    }

    // Caller owns the single operation admission for capture plus downstream encoding or file output.
    internal ValueTask<SchematicDocument?> CaptureDocumentAsync(SchematicBounds bounds, CancellationToken cancellationToken)
    {
        bounds.Validate();
        return DispatchAsync<SchematicDocument?>(() => source.Capture(bounds), null, cancellationToken);
    }

    private ValueTask<T> DispatchAsync<T>(Func<T> action, T unavailable, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dispatcher.IsGameThreadKnown) return ValueTask.FromResult(unavailable);
        if (dispatcher.IsGameThread) return ValueTask.FromResult(action());
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("schematic.world-operation", () =>
        {
            if (cancellationToken.IsCancellationRequested) completion.TrySetCanceled(cancellationToken);
            else
            {
                try { completion.TrySetResult(action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }
        })) return ValueTask.FromResult(unavailable);
        return new(completion.Task);
    }
}
