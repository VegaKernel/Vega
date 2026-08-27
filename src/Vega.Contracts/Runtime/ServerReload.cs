namespace Vega.Contracts.Runtime;

public sealed record ServerReloading(string RequestedBy);

public sealed record ServerReloaded(
    string RequestedBy,
    DateTimeOffset CompletedAt);

public enum ServerReloadStatus
{
    Succeeded,
    Cancelled,
    Failed
}

public sealed record ServerReloadResult(
    ServerReloadStatus Status,
    string Message,
    string? ErrorCode = null)
{
    public bool IsSuccess => Status == ServerReloadStatus.Succeeded;
}

public interface IServerReloadService
{
    ValueTask<ServerReloadResult> ReloadAsync(
        string requestedBy,
        CancellationToken cancellationToken = default);
}
