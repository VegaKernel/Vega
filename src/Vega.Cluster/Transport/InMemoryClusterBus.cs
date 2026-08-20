using Vega.Cluster.Dispatching;
using Vega.Contracts.Cluster;

namespace Vega.Cluster.Transport;

public sealed class InMemoryClusterBus
{
    private readonly object sync = new();
    private readonly Dictionary<ServerId, InMemoryClusterEndpoint> endpoints = [];

    public InMemoryClusterEndpoint Connect(ServerId serverId, ClusterMessageDispatcher dispatcher)
    {
        lock (sync)
        {
            if (endpoints.ContainsKey(serverId))
            {
                throw new InvalidOperationException($"Cluster server '{serverId}' is already connected.");
            }

            var endpoint = new InMemoryClusterEndpoint(this, serverId, dispatcher);
            endpoints.Add(serverId, endpoint);
            return endpoint;
        }
    }

    internal void Disconnect(ServerId serverId)
    {
        lock (sync)
        {
            endpoints.Remove(serverId);
        }
    }

    internal InMemoryClusterEndpoint[] Peers(ServerId sender)
    {
        lock (sync)
        {
            return endpoints
                .Where(pair => pair.Key != sender)
                .Select(static pair => pair.Value)
                .ToArray();
        }
    }

    internal bool TryGet(ServerId serverId, out InMemoryClusterEndpoint? endpoint)
    {
        lock (sync)
        {
            return endpoints.TryGetValue(serverId, out endpoint);
        }
    }
}

public sealed class InMemoryClusterEndpoint :
    IClusterPublisher,
    IClusterCommandSender,
    IClusterRequestSender,
    IDisposable
{
    private readonly InMemoryClusterBus bus;
    private readonly ClusterMessageDispatcher dispatcher;
    private bool disposed;

    internal InMemoryClusterEndpoint(
        InMemoryClusterBus bus,
        ServerId serverId,
        ClusterMessageDispatcher dispatcher)
    {
        this.bus = bus;
        ServerId = serverId;
        this.dispatcher = dispatcher;
    }

    public ServerId ServerId { get; }

    public async ValueTask<ClusterDeliveryResult> PublishAsync<TEvent>(
        ClusterEnvelope<TEvent> envelope,
        CancellationToken cancellationToken = default)
        where TEvent : IClusterEvent
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (envelope.Metadata.SenderServerId != ServerId)
        {
            return new ClusterDeliveryResult(
                ClusterDeliveryStatus.Rejected,
                "Envelope sender does not match the publishing endpoint.");
        }

        ClusterDeliveryResult? failure = null;
        bool sawPeer = false;
        bool sawDelivered = false;
        foreach (InMemoryClusterEndpoint peer in bus.Peers(ServerId))
        {
            sawPeer = true;
            ClusterDeliveryResult delivery = await peer.dispatcher
                .DispatchEventAsync(envelope, cancellationToken)
                .ConfigureAwait(false);
            if (!delivery.IsDelivered)
            {
                failure = delivery;
            }
            else if (delivery.Status == ClusterDeliveryStatus.Delivered)
            {
                sawDelivered = true;
            }
        }

        if (failure is ClusterDeliveryResult failed)
        {
            return failed;
        }

        return sawPeer && !sawDelivered
            ? ClusterDeliveryResult.Duplicate()
            : ClusterDeliveryResult.Delivered();
    }

    public ValueTask<ClusterDeliveryResult> SendAsync<TCommand>(
        ServerId target,
        ClusterEnvelope<TCommand> envelope,
        CancellationToken cancellationToken = default)
        where TCommand : IClusterCommand
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (envelope.Metadata.SenderServerId != ServerId)
        {
            return ValueTask.FromResult(new ClusterDeliveryResult(
                ClusterDeliveryStatus.Rejected,
                "Envelope sender does not match the sending endpoint."));
        }

        return bus.TryGet(target, out InMemoryClusterEndpoint? endpoint) && endpoint is not null
            ? endpoint.dispatcher.DispatchCommandAsync(envelope, cancellationToken)
            : ValueTask.FromResult(ClusterDeliveryResult.Unavailable($"Cluster server '{target}' is unavailable."));
    }

    public async ValueTask<ClusterRequestResult<TResponse>> RequestAsync<TRequest, TResponse>(
        ServerId target,
        ClusterEnvelope<TRequest> envelope,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        where TRequest : IClusterRequest<TResponse>
        where TResponse : IClusterResponse
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (envelope.Metadata.SenderServerId != ServerId)
        {
            return new ClusterRequestResult<TResponse>(
                new ClusterDeliveryResult(
                    ClusterDeliveryStatus.Rejected,
                    "Envelope sender does not match the requesting endpoint."),
                null);
        }

        if (!bus.TryGet(target, out InMemoryClusterEndpoint? endpoint) || endpoint is null)
        {
            return new ClusterRequestResult<TResponse>(
                ClusterDeliveryResult.Unavailable($"Cluster server '{target}' is unavailable."),
                default);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            ClusterMessageDispatcher.ClusterResponseDispatchResult<TResponse> dispatched =
                await endpoint.dispatcher
                .DispatchRequestAsync<TRequest, TResponse>(envelope, timeoutCancellation.Token)
                .ConfigureAwait(false);
            if (dispatched.Response is null)
            {
                return new ClusterRequestResult<TResponse>(dispatched.Delivery, null);
            }

            ClusterEnvelope<TResponse> response = new ClusterMessageFactory(endpoint.ServerId)
                .Create(dispatched.Response, envelope.Metadata.CorrelationId);
            return new ClusterRequestResult<TResponse>(dispatched.Delivery, response);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClusterRequestResult<TResponse>(
                new ClusterDeliveryResult(ClusterDeliveryStatus.TimedOut, "Cluster request timed out."),
                default);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        bus.Disconnect(ServerId);
    }
}
