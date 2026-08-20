using Vega.Contracts.Cluster;

namespace Vega.Cluster.Transport;

public sealed class UnavailableClusterClient :
    IClusterPublisher,
    IClusterCommandSender,
    IClusterRequestSender
{
    public static UnavailableClusterClient Instance { get; } = new();

    public ValueTask<ClusterDeliveryResult> PublishAsync<TEvent>(
        ClusterEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default)
        where TEvent : IClusterEvent =>
        ValueTask.FromResult(ClusterDeliveryResult.Unavailable("No cluster transport is configured."));

    public ValueTask<ClusterDeliveryResult> SendAsync<TCommand>(
        ServerId target,
        ClusterEnvelope<TCommand> envelope,
        CancellationToken cancellationToken = default)
        where TCommand : IClusterCommand =>
        ValueTask.FromResult(ClusterDeliveryResult.Unavailable("No cluster transport is configured."));

    public ValueTask<ClusterRequestResult<TResponse>> RequestAsync<TRequest, TResponse>(
        ServerId target,
        ClusterEnvelope<TRequest> envelope,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        where TRequest : IClusterRequest<TResponse>
        where TResponse : IClusterResponse =>
        ValueTask.FromResult(new ClusterRequestResult<TResponse>(
            ClusterDeliveryResult.Unavailable("No cluster transport is configured."),
            default));
}
