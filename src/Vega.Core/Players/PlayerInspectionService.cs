using Vega.Contracts.Players;

namespace Vega.Core.Players;

public sealed class PlayerInspectionService(
    IPlayerDirectory players,
    IPlayerInventoryService inventories) : IPlayerInspectionService
{
    public ValueTask<PlayerInspectionSnapshot?> CaptureAsync(
        PlayerId playerId,
        PlayerInspectionExposure exposure = PlayerInspectionExposure.Redacted,
        CancellationToken cancellationToken = default) => players.TryGet(playerId, out var player) && player is not null
        ? CaptureAsync(player, exposure, cancellationToken) : ValueTask.FromResult<PlayerInspectionSnapshot?>(null);

    public async ValueTask<PlayerInspectionSnapshot?> CaptureAsync(PlayerSnapshot player, PlayerInspectionExposure exposure = PlayerInspectionExposure.Redacted,
        CancellationToken cancellationToken = default)
    {
        if (!IsCurrent(player))
        {
            return null;
        }

        PlayerInventorySnapshot? inventory = await inventories
            .CaptureAsync(player, cancellationToken)
            .ConfigureAwait(false);
        if (inventory is null || !IsCurrent(player) || inventory.SessionId.HasValue && (inventory.SessionId != player.SessionId || inventory.WorldId != player.WorldId))
        {
            return null;
        }

        return new PlayerInspectionSnapshot(
            exposure == PlayerInspectionExposure.Sensitive
                ? player
                : PlayerSnapshotPrivacy.RedactSensitive(player),
            inventory);
    }

    private bool IsCurrent(PlayerSnapshot player) => players.TryGet(player.Id, out var current) && current is not null &&
        current.SessionId == player.SessionId && current.WorldId == player.WorldId;
}
