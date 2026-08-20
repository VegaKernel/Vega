using Vega.Contracts.Players;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Events;

namespace Vega.Cluster.Scenarios.Chat;

public sealed class AllowInterServerChatPolicy : IInterServerChatPolicy
{
    public bool ShouldDeliver(ClusterEnvelope<InterServerChatPublished> envelope) => true;
}

public sealed class InterServerChatHandler(
    IInterServerChatPolicy policy,
    IPlayerDirectory players,
    IPlayerMessenger messenger) : IClusterEventHandler<InterServerChatPublished>
{
    public async ValueTask HandleAsync(
        ClusterEnvelope<InterServerChatPublished> envelope,
        CancellationToken cancellationToken)
    {
        if (!policy.ShouldDeliver(envelope))
        {
            return;
        }

        string text = $"[{envelope.Metadata.SenderServerId}] {envelope.Message.SenderName}: {envelope.Message.Text}";
        foreach (PlayerSnapshot player in players.SnapshotOnlinePlayers())
        {
            await messenger.SendMessageAsync(player.Id, text, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class InterServerChatPublisher(
    IClusterPublisher publisher,
    ClusterMessageFactory messages)
{
    public ValueTask<ClusterDeliveryResult> PublishAsync(
        string senderName,
        string text,
        string channel = "global",
        CancellationToken cancellationToken = default) =>
        publisher.PublishAsync(
            messages.Create(new InterServerChatPublished(senderName, text, channel)),
            cancellationToken);
}
