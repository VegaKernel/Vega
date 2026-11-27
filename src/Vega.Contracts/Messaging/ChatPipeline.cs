using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Messaging;

public sealed record ChatMessageContext(
    WorldId WorldId,
    PlayerSnapshot Sender,
    string Content,
    IReadOnlyList<PlayerSnapshot> Recipients);

public readonly record struct ChatContentTransformResult(string Content, bool Changed = false);

public readonly record struct ChatModerationDecision(bool Allowed, string? Reason = null)
{
    public static ChatModerationDecision Allow { get; } = new(true);

    public static ChatModerationDecision Reject(string reason) => new(false, reason);
}

public readonly record struct ChatRecipientRenderResult(string Content, bool Changed = false);

public readonly record struct ChatColor(byte Red, byte Green, byte Blue);

public sealed record ChatDelivery(PlayerId RecipientId, string Content)
{
    public ChatColor? Color { get; init; }
    public string? SenderDisplayName { get; init; }
}

public sealed record ChatPipelineResult(
    bool Allowed,
    bool RequiresCustomDelivery,
    IReadOnlyList<ChatDelivery> Deliveries,
    string? RejectionReason = null)
{
    /// <summary>Accepted common text before any recipient-specific rendering.</summary>
    public string? PublicContent { get; init; }
}

/// <summary>Accepted ordinary public chat; this is not a delivery acknowledgement.</summary>
public readonly record struct PublicChatAcceptedObservation(WorldId WorldId, PlayerId PlayerId,
    PlayerSessionId SessionId, string Content, int RecipientCount, bool RequiresCustomDelivery);

public interface IPublicChatObservations
{
    event Action<PublicChatAcceptedObservation>? Accepted;
}

/// <summary>Host-only notification after the native public chat hook accepts a message.</summary>
public interface IPublicChatObservationSink
{
    void ObserveAccepted(ChatMessageContext context, ChatPipelineResult result);
}

public interface IChatContentTransform
{
    int Order => 0;

    ChatContentTransformResult Transform(ChatMessageContext context, string content);
}

public interface IChatModerationRule
{
    int Order => 0;

    ChatModerationDecision Evaluate(ChatMessageContext context, string content);
}

public interface IChatRecipientRenderer
{
    int Order => 0;

    ChatRecipientRenderResult Render(
        ChatMessageContext context,
        PlayerSnapshot recipient,
        string content);
}

public interface IChatPipeline
{
    ChatPipelineResult Process(ChatMessageContext context);
}
