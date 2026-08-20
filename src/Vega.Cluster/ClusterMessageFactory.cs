using Vega.Contracts.Cluster;

namespace Vega.Cluster;

public sealed class ClusterMessageFactory(ServerId senderServerId, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public ClusterEnvelope<TMessage> Create<TMessage>(
        TMessage message,
        ClusterCorrelationId? correlationId = null)
        where TMessage : IClusterMessage =>
        new(
            new ClusterMessageMetadata(
                ClusterMessageId.New(),
                correlationId,
                senderServerId,
                time.GetUtcNow(),
                message.Schema,
                message.SchemaVersion),
            message);
}
