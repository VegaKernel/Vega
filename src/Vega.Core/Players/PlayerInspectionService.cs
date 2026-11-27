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
        if (FindCurrent(player) is null)
        {
            return null;
        }

        PlayerInventorySnapshot? inventory = await inventories
            .CaptureAsync(player, cancellationToken)
            .ConfigureAwait(false);
        PlayerSnapshot? current = FindCurrent(player);
        if (inventory is null || current is null || inventory.SessionId.HasValue && (inventory.SessionId != player.SessionId || inventory.WorldId != player.WorldId))
        {
            return null;
        }

        return new PlayerInspectionSnapshot(
            exposure == PlayerInspectionExposure.Sensitive
                ? current
                : PlayerSnapshotPrivacy.RedactSensitive(current),
            inventory);
    }

    private PlayerSnapshot? FindCurrent(PlayerSnapshot player) => players.TryGet(player.Id, out var current) && current is not null &&
        current.SessionId == player.SessionId && current.WorldId == player.WorldId ? current : null;
}
