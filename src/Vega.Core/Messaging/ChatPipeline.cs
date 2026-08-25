using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.Core.Messaging;

public sealed class ChatPipeline : IChatPipeline, IOperationsLogSource
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

    public event Action<OperationsLogMessage>? LogProduced;

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
                string reason = decision.Reason ?? "chat.rejected";
                Publish(new OperationsLogMessage(
                    OperationsLogLevel.Warning,
                    "chat",
                    $"{Sanitize(context.Sender.Name)}: {Sanitize(content)} [rejected: {Sanitize(reason)}]"));
                return new ChatPipelineResult(
                    false,
                    true,
                    Array.Empty<ChatDelivery>(),
                    reason);
            }
        }

        var deliveries = new ChatDelivery[context.Recipients.Count];
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
                $"{context.Sender.Name}: {rendered}");
        }

        Publish(new OperationsLogMessage(
            OperationsLogLevel.Information,
            "chat",
            $"{Sanitize(context.Sender.Name)}: {Sanitize(content)}"));
        return new ChatPipelineResult(
            true,
            changed,
            Array.AsReadOnly(deliveries));
    }

    private void Publish(OperationsLogMessage message)
    {
        Action<OperationsLogMessage>? subscribers = LogProduced;
        if (subscribers is null)
        {
            return;
        }

        foreach (Delegate subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((Action<OperationsLogMessage>)subscriber)(message);
            }
            catch
            {
                // Observability must never change chat delivery or block later observers.
            }
        }
    }

    private static string Sanitize(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

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
