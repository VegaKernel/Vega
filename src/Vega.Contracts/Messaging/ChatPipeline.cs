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

public sealed record ChatDelivery(PlayerId RecipientId, string Content);

public sealed record ChatPipelineResult(
    bool Allowed,
    bool RequiresCustomDelivery,
    IReadOnlyList<ChatDelivery> Deliveries,
    string? RejectionReason = null);

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
