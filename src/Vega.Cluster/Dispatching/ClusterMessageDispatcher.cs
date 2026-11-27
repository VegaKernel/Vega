using Vega.Contracts.Cluster;

namespace Vega.Cluster.Dispatching;

public sealed class ClusterMessageDispatcher
{
    private readonly Dictionary<Type, List<Func<object, CancellationToken, ValueTask>>> eventHandlers = [];
    private readonly Dictionary<Type, Func<object, CancellationToken, ValueTask>> commandHandlers = [];
    private readonly Dictionary<Type, Func<object, CancellationToken, ValueTask<object>>> requestHandlers = [];
    private readonly ClusterMessageDeduplicator deduplicator;
    private readonly ClusterRequestResponseCache requestResponses = new();
    private readonly IClusterAuditSink audit;

    public ClusterMessageDispatcher(
        ClusterMessageDeduplicator deduplicator,
        IClusterAuditSink? audit = null)
    {
        this.deduplicator = deduplicator ?? throw new ArgumentNullException(nameof(deduplicator));
        this.audit = audit ?? NullClusterAuditSink.Instance;
    }

    public void RegisterEvent<TEvent>(IClusterEventHandler<TEvent> handler)
        where TEvent : IClusterEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!eventHandlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            handlers = [];
            eventHandlers.Add(typeof(TEvent), handlers);
        }

        handlers.Add((envelope, cancellationToken) =>
            handler.HandleAsync((ClusterEnvelope<TEvent>)envelope, cancellationToken));
    }

    public void RegisterCommand<TCommand>(IClusterCommandHandler<TCommand> handler)
        where TCommand : IClusterCommand
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!commandHandlers.TryAdd(
            typeof(TCommand),
            (envelope, cancellationToken) =>
                handler.HandleAsync((ClusterEnvelope<TCommand>)envelope, cancellationToken)))
        {
            throw new InvalidOperationException($"Cluster command handler for {typeof(TCommand).Name} is already registered.");
        }
    }

    public void RegisterRequest<TRequest, TResponse>(IClusterRequestHandler<TRequest, TResponse> handler)
        where TRequest : IClusterRequest<TResponse>
        where TResponse : IClusterResponse
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!requestHandlers.TryAdd(
            typeof(TRequest),
            async (envelope, cancellationToken) =>
                await handler
                    .HandleAsync((ClusterEnvelope<TRequest>)envelope, cancellationToken)
                    .ConfigureAwait(false)))
        {
            throw new InvalidOperationException($"Cluster request handler for {typeof(TRequest).Name} is already registered.");
        }
    }

    public async ValueTask<ClusterDeliveryResult> DispatchEventAsync<TEvent>(
        ClusterEnvelope<TEvent> envelope,
        CancellationToken cancellationToken)
        where TEvent : IClusterEvent
    {
        if (!deduplicator.TryAccept(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId))
        {
            Audit(envelope.Metadata, "event", typeof(TEvent), ClusterDeliveryStatus.Duplicate);
            return ClusterDeliveryResult.Duplicate();
        }

        if (!eventHandlers.TryGetValue(typeof(TEvent), out var handlers) || handlers.Count == 0)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            return Reject(envelope.Metadata, "event", typeof(TEvent), "No handler registered.");
        }

        try
        {
            foreach (var handler in handlers)
            {
                await handler(envelope, cancellationToken).ConfigureAwait(false);
            }

            Audit(envelope.Metadata, "event", typeof(TEvent), ClusterDeliveryStatus.Delivered);
            return ClusterDeliveryResult.Delivered();
        }
        catch (OperationCanceledException)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            Audit(envelope.Metadata, "event", typeof(TEvent), ClusterDeliveryStatus.Failed, ex.Message);
            return new ClusterDeliveryResult(ClusterDeliveryStatus.Failed, ex.Message);
        }
    }

    public async ValueTask<ClusterDeliveryResult> DispatchCommandAsync<TCommand>(
        ClusterEnvelope<TCommand> envelope,
        CancellationToken cancellationToken)
        where TCommand : IClusterCommand
    {
        if (!deduplicator.TryAccept(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId))
        {
            Audit(envelope.Metadata, "command", typeof(TCommand), ClusterDeliveryStatus.Duplicate);
            return ClusterDeliveryResult.Duplicate();
        }

        if (!commandHandlers.TryGetValue(typeof(TCommand), out var handler))
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            return Reject(envelope.Metadata, "command", typeof(TCommand), "No handler registered.");
        }

        try
        {
            await handler(envelope, cancellationToken).ConfigureAwait(false);
            Audit(envelope.Metadata, "command", typeof(TCommand), ClusterDeliveryStatus.Delivered);
            return ClusterDeliveryResult.Delivered();
        }
        catch (OperationCanceledException)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            Audit(envelope.Metadata, "command", typeof(TCommand), ClusterDeliveryStatus.Failed, ex.Message);
            return new ClusterDeliveryResult(ClusterDeliveryStatus.Failed, ex.Message);
        }
    }

    public async ValueTask<ClusterResponseDispatchResult<TResponse>> DispatchRequestAsync<TRequest, TResponse>(
        ClusterEnvelope<TRequest> envelope,
        CancellationToken cancellationToken)
        where TRequest : IClusterRequest<TResponse>
        where TResponse : IClusterResponse
    {
        if (!deduplicator.TryAccept(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId))
        {
            requestResponses.TryGet(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId, out TResponse? cached);
            Audit(envelope.Metadata, "request", typeof(TRequest), ClusterDeliveryStatus.Duplicate);
            return new ClusterResponseDispatchResult<TResponse>(ClusterDeliveryResult.Duplicate(), cached);
        }

        if (!requestHandlers.TryGetValue(typeof(TRequest), out var handler))
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            ClusterDeliveryResult rejected = Reject(
                envelope.Metadata,
                "request",
                typeof(TRequest),
                "No handler registered.");
            return new ClusterResponseDispatchResult<TResponse>(rejected, default);
        }

        try
        {
            object response = await handler(envelope, cancellationToken).ConfigureAwait(false);
            requestResponses.Store(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId, (TResponse)response);
            Audit(envelope.Metadata, "request", typeof(TRequest), ClusterDeliveryStatus.Delivered);
            return new ClusterResponseDispatchResult<TResponse>(ClusterDeliveryResult.Delivered(), (TResponse)response);
        }
        catch (OperationCanceledException)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            deduplicator.Forget(envelope.Metadata.SenderServerId, envelope.Metadata.MessageId);
            Audit(envelope.Metadata, "request", typeof(TRequest), ClusterDeliveryStatus.Failed, ex.Message);
            return new ClusterResponseDispatchResult<TResponse>(
                new ClusterDeliveryResult(ClusterDeliveryStatus.Failed, ex.Message),
                default);
        }
    }

    public readonly record struct ClusterResponseDispatchResult<TResponse>(
        ClusterDeliveryResult Delivery,
        TResponse? Response)
        where TResponse : IClusterResponse;

    private ClusterDeliveryResult Reject(
        ClusterMessageMetadata metadata,
        string family,
        Type messageType,
        string detail)
    {
        Audit(metadata, family, messageType, ClusterDeliveryStatus.Rejected, detail);
        return new ClusterDeliveryResult(ClusterDeliveryStatus.Rejected, detail);
    }

    private void Audit(
        ClusterMessageMetadata metadata,
        string family,
        Type messageType,
        ClusterDeliveryStatus status,
        string? detail = null) =>
        audit.Record(new ClusterAuditRecord(metadata, family, messageType.Name, status, detail));

    private sealed class NullClusterAuditSink : IClusterAuditSink
    {
        public static NullClusterAuditSink Instance { get; } = new();

        public void Record(ClusterAuditRecord record)
        {
        }
    }
}
