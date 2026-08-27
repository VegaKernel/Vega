namespace Vega.Contracts.Worlds;

public sealed record AnglerQuestClearRequest(
    WorldId WorldId,
    string? PlayerName,
    string Actor);

public enum AnglerQuestClearStatus
{
    Applied,
    NotFound,
    Invalid,
    DispatcherUnavailable,
    Cancelled,
    Failed
}

public sealed record AnglerQuestClearResult(
    AnglerQuestClearStatus Status,
    int RemovedEntries = 0,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == AnglerQuestClearStatus.Applied;
}

public interface IWorldAnglerQuestService
{
    ValueTask<AnglerQuestClearResult> ClearAsync(
        AnglerQuestClearRequest request,
        CancellationToken cancellationToken = default);
}
