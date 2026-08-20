namespace Vega.Contracts.Cluster.Events;

public sealed record InterServerChatPublished(
    string SenderName,
    string Text,
    string Channel) : IClusterEvent
{
    public string Schema => "vega.cluster.chat.published";

    public int SchemaVersion => 1;
}

public interface IInterServerChatPolicy
{
    bool ShouldDeliver(ClusterEnvelope<InterServerChatPublished> envelope);
}
