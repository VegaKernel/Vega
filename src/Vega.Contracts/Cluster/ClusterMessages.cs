namespace Vega.Contracts.Cluster;

public interface IClusterMessage
{
    string Schema { get; }

    int SchemaVersion { get; }
}

public interface IClusterEvent : IClusterMessage;

public interface IClusterCommand : IClusterMessage;

public interface IClusterResponse : IClusterMessage;

public interface IClusterRequest : IClusterMessage;

public interface IClusterRequest<TResponse> : IClusterRequest
    where TResponse : IClusterResponse;

public sealed record ClusterMessageMetadata(
    ClusterMessageId MessageId,
    ClusterCorrelationId? CorrelationId,
    ServerId SenderServerId,
    DateTimeOffset Timestamp,
    string Schema,
    int MessageVersion);

public sealed record ClusterEnvelope<TMessage>
    where TMessage : IClusterMessage
{
    public ClusterEnvelope(ClusterMessageMetadata metadata, TMessage message)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(message);
        if (metadata.MessageId.Value == Guid.Empty)
        {
            throw new ArgumentException("Cluster message ID must not be empty.", nameof(metadata));
        }

        if (metadata.CorrelationId is ClusterCorrelationId correlationId && correlationId.Value == Guid.Empty)
        {
            throw new ArgumentException("Cluster correlation ID must not be empty.", nameof(metadata));
        }

        if (string.IsNullOrWhiteSpace(metadata.SenderServerId.Value))
        {
            throw new ArgumentException("Cluster sender server ID must not be empty.", nameof(metadata));
        }

        if (string.IsNullOrWhiteSpace(message.Schema) || message.SchemaVersion <= 0)
        {
            throw new ArgumentException("Cluster message schema and version must be valid.", nameof(message));
        }

        if (!string.Equals(metadata.Schema, message.Schema, StringComparison.Ordinal) ||
            metadata.MessageVersion != message.SchemaVersion)
        {
            throw new ArgumentException("Cluster metadata schema/version does not match the typed message.", nameof(metadata));
        }

        Metadata = metadata;
        Message = message;
    }

    public ClusterMessageMetadata Metadata { get; }

    public TMessage Message { get; }
}

public enum ClusterDeliveryStatus
{
    Delivered,
    Duplicate,
    Unavailable,
    TimedOut,
    Rejected,
    Failed
}

public readonly record struct ClusterDeliveryResult(
    ClusterDeliveryStatus Status,
    string? Error = null)
{
    public bool IsDelivered => Status is ClusterDeliveryStatus.Delivered or ClusterDeliveryStatus.Duplicate;

    public static ClusterDeliveryResult Delivered() => new(ClusterDeliveryStatus.Delivered);

    public static ClusterDeliveryResult Duplicate() => new(ClusterDeliveryStatus.Duplicate);

    public static ClusterDeliveryResult Unavailable(string? error = null) =>
        new(ClusterDeliveryStatus.Unavailable, error);
}

public readonly record struct ClusterRequestResult<TResponse>(
    ClusterDeliveryResult Delivery,
    ClusterEnvelope<TResponse>? Response)
    where TResponse : IClusterResponse;
