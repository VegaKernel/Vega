using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Core.Messaging;

public sealed class ChatPipeline : IChatPipeline
{
    private readonly IChatContentTransform[] transforms;
    private readonly IChatModerationRule[] moderation;
    private readonly IChatRecipientRenderer[] renderers;

    public ChatPipeline(
        IEnumerable<IChatContentTransform>? transforms = null,
        IEnumerable<IChatModerationRule>? moderation = null,
        IEnumerable<IChatRecipientRenderer>? renderers = null)
    {
        this.transforms = Order(transforms ?? []);
        this.moderation = Order(moderation ?? []);
        this.renderers = Order(renderers ?? []);
    }

    public ChatPipelineResult Process(ChatMessageContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string content = context.Content;
        bool changed = false;
        foreach (IChatContentTransform transform in transforms)
        {
            ChatContentTransformResult result = transform.Transform(context, content);
            content = result.Content ?? string.Empty;
            changed |= result.Changed;
        }

        foreach (IChatModerationRule rule in moderation)
        {
            ChatModerationDecision decision = rule.Evaluate(context, content);
            if (!decision.Allowed)
            {
                return new ChatPipelineResult(
                    false,
                    true,
                    Array.Empty<ChatDelivery>(),
                    decision.Reason ?? "chat.rejected");
            }
        }

        var deliveries = new ChatDelivery[context.Recipients.Count];
        string? defaultDelivery = null;
        for (var index = 0; index < context.Recipients.Count; index++)
        {
            PlayerSnapshot recipient = context.Recipients[index];
            string rendered = content;
            foreach (IChatRecipientRenderer renderer in renderers)
            {
                ChatRecipientRenderResult result = renderer.Render(context, recipient, rendered);
                rendered = result.Content ?? string.Empty;
                changed |= result.Changed;
            }

            deliveries[index] = new ChatDelivery(
                recipient.Id,
                rendered == content
                    ? defaultDelivery ??= $"{context.Sender.Name}: {content}"
                    : $"{context.Sender.Name}: {rendered}");
        }

        return new ChatPipelineResult(
            true,
            changed,
            Array.AsReadOnly(deliveries));
    }

    private static T[] Order<T>(IEnumerable<T> stages) => stages
        .Select((stage, index) => (Stage: stage, Index: index))
        .OrderBy(static entry => entry.Stage switch
        {
            IChatContentTransform transform => transform.Order,
            IChatModerationRule rule => rule.Order,
            IChatRecipientRenderer renderer => renderer.Order,
            _ => 0
        })
        .ThenBy(static entry => entry.Index)
        .Select(static entry => entry.Stage)
        .ToArray();
}
