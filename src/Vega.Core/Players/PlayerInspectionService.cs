using Vega.Contracts.Players;

namespace Vega.Core.Players;

public sealed class PlayerInspectionService(
    IPlayerDirectory players,
    IPlayerInventoryService inventories) : IPlayerInspectionService
{
    public async ValueTask<PlayerInspectionSnapshot?> CaptureAsync(
        PlayerId playerId,
        PlayerInspectionExposure exposure = PlayerInspectionExposure.Redacted,
        CancellationToken cancellationToken = default)
    {
        if (!players.TryGet(playerId, out PlayerSnapshot? player) || player is null)
        {
            return null;
        }

        PlayerInventorySnapshot? inventory = await inventories
            .CaptureAsync(playerId, cancellationToken)
            .ConfigureAwait(false);
        if (inventory is null)
        {
            return null;
        }

        return new PlayerInspectionSnapshot(
            exposure == PlayerInspectionExposure.Sensitive
                ? player
                : PlayerSnapshotPrivacy.RedactSensitive(player),
            inventory);
    }
}
