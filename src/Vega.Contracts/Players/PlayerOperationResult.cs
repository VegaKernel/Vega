namespace Vega.Contracts.Players;

public enum PlayerOperationStatus
{
    Applied = 0,
    PlayerNotFound = 1,
    DispatcherUnavailable = 2,
    Cancelled = 3,
    Failed = 4
}

public readonly record struct PlayerOperationResult(
    PlayerOperationStatus Status,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == PlayerOperationStatus.Applied;
    public int? DeliveredItemStack { get; init; }
    public string? DeliveredItemName { get; init; }

    public static PlayerOperationResult Applied() => new(PlayerOperationStatus.Applied);

    public static PlayerOperationResult NotFound() =>
        new(PlayerOperationStatus.PlayerNotFound, "player.not_found");

    public static PlayerOperationResult DispatcherUnavailable() =>
        new(PlayerOperationStatus.DispatcherUnavailable, "player.dispatcher_unavailable");

    public static PlayerOperationResult Cancelled() =>
        new(PlayerOperationStatus.Cancelled, "player.operation_cancelled");

    public static PlayerOperationResult Failed(string errorCode) =>
        new(PlayerOperationStatus.Failed, errorCode);
}
