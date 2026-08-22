using Vega.Contracts.Events;
using Vega.Contracts.Worlds;

namespace Vega.Core.Worlds;

public sealed class ChestService(
    IChestBackend backend,
    IAsyncEventPipeline<ChestChanging> changing,
    IAsyncEventPipeline<ChestChanged> changed) : IChestService
{
    public ValueTask<ChestSnapshot?> CaptureAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default) =>
        backend.CaptureAsync(identity, cancellationToken);

    public async ValueTask<ChestMutationResult> ReplaceAsync(
        ChestMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ChestSnapshot? current = await backend
            .CaptureAsync(request.Identity, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return new ChestMutationResult(
                ChestMutationStatus.NotFound,
                ErrorCode: "chest.not_found");
        }

        EventOutcome<ChestChanging> decision = await changing
            .DispatchAsync(new ChestChanging(request, current), cancellationToken)
            .ConfigureAwait(false);
        if (decision.IsCancelled)
        {
            return new ChestMutationResult(
                ChestMutationStatus.Rejected,
                current,
                ErrorCode: decision.Cancellation?.Code ?? "chest.rejected");
        }

        ChestMutationResult result = await backend
            .ReplaceAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (result.IsSuccess && result.Before is not null && result.After is not null)
        {
            _ = await changed.DispatchAsync(
                new ChestChanged(request, result.Before, result.After),
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
