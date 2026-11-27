using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Authorization;

namespace Vega.Core.Messaging;

public sealed class ChatPipeline : IChatPipeline, IPublicChatObservations, IPublicChatObservationSink
{
    public event Action<PublicChatAcceptedObservation>? Accepted;
    private readonly IChatContentTransform[] transforms;
    private readonly IChatModerationRule[] moderation;
    private readonly IChatRecipientRenderer[] renderers;
    private readonly Func<PlayerSnapshot, GroupChatPresentation?>? presentation;

    public ChatPipeline(
        IEnumerable<IChatContentTransform>? transforms = null,
        IEnumerable<IChatModerationRule>? moderation = null,
        IEnumerable<IChatRecipientRenderer>? renderers = null,
        Func<PlayerSnapshot, GroupChatPresentation?>? presentation = null)
    {
        this.transforms = Order(transforms ?? []);
        this.moderation = Order(moderation ?? []);
        this.renderers = Order(renderers ?? []);
        this.presentation = presentation;
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

        GroupChatPresentation? style = presentation?.Invoke(context.Sender);
        string senderName = context.Sender.Name;
        ChatColor? color = null;
        if (style is { IsValid: true } && style != GroupChatPresentation.Default)
        {
            senderName = style.Prefix + context.Sender.Name + style.Suffix;
            if (style.Color is { } hex)
                color = new ChatColor(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
            changed |= senderName != context.Sender.Name || color.HasValue;
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
                    ? defaultDelivery ??= $"{senderName}: {content}"
                    : $"{senderName}: {rendered}")
            {
                Color = color,
                SenderDisplayName = senderName != context.Sender.Name ? senderName : null
            };
        }

        return new ChatPipelineResult(
            true,
            changed,
            Array.AsReadOnly(deliveries)) { PublicContent = content };
    }

    public void ObserveAccepted(ChatMessageContext context, ChatPipelineResult result)
    {
        if (Accepted is not { } handlers || !result.Allowed || result.PublicContent is not { } content ||
            context.Sender.WorldId != context.WorldId || context.Sender.SessionId is not { } sessionId ||
            string.IsNullOrWhiteSpace(context.Content) || context.Content.TrimStart()[0] is '/' or '.') return;
        var observation = new PublicChatAcceptedObservation(context.WorldId, context.Sender.Id,
            sessionId, content, result.Deliveries.Count, result.RequiresCustomDelivery);
        foreach (Action<PublicChatAcceptedObservation> handler in handlers.GetInvocationList())
        {
            try { handler(observation); }
            catch (Exception) { System.Diagnostics.Trace.TraceWarning("Public chat observation callback failed."); }
        }
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
