namespace Vega.Contracts.Cluster;

public interface IClusterPublisher
{
    ValueTask<ClusterDeliveryResult> PublishAsync<TEvent>(
        ClusterEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default)
        where TEvent : IClusterEvent;
}

public interface IClusterCommandSender
{
    ValueTask<ClusterDeliveryResult> SendAsync<TCommand>(
        ServerId target,
        ClusterEnvelope<TCommand> envelope,
        CancellationToken cancellationToken = default)
        where TCommand : IClusterCommand;
}

public interface IClusterRequestSender
{
    ValueTask<ClusterRequestResult<TResponse>> RequestAsync<TRequest, TResponse>(
        ServerId target,
        ClusterEnvelope<TRequest> envelope,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        where TRequest : IClusterRequest<TResponse>
        where TResponse : IClusterResponse;
}

public interface IClusterEventHandler<TEvent>
    where TEvent : IClusterEvent
{
    ValueTask HandleAsync(
        ClusterEnvelope<TEvent> envelope,
        CancellationToken cancellationToken);
}

public interface IClusterCommandHandler<TCommand>
    where TCommand : IClusterCommand
{
    ValueTask HandleAsync(
        ClusterEnvelope<TCommand> envelope,
        CancellationToken cancellationToken);
}

public interface IClusterRequestHandler<TRequest, TResponse>
    where TRequest : IClusterRequest<TResponse>
    where TResponse : IClusterResponse
{
    ValueTask<TResponse> HandleAsync(
        ClusterEnvelope<TRequest> envelope,
        CancellationToken cancellationToken);
}

public sealed record ClusterAuditRecord(
    ClusterMessageMetadata Metadata,
    string Family,
    string MessageType,
    ClusterDeliveryStatus Status,
    string? Detail = null);

public interface IClusterAuditSink
{
    void Record(ClusterAuditRecord record);
}
