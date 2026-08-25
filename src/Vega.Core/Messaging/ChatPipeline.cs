using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.Core.Messaging;

public sealed class ChatPipeline : IChatPipeline
{
    private readonly IChatContentTransform[] transforms;
    private readonly IChatModerationRule[] moderation;
    private readonly IChatRecipientRenderer[] renderers;
    private readonly IOperationsLogger? logger;

    public ChatPipeline(
        IEnumerable<IChatContentTransform>? transforms = null,
        IEnumerable<IChatModerationRule>? moderation = null,
        IEnumerable<IChatRecipientRenderer>? renderers = null,
        IOperationsLogger? logger = null)
    {
        this.transforms = Order(transforms ?? []);
        this.moderation = Order(moderation ?? []);
        this.renderers = Order(renderers ?? []);
        this.logger = logger;
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
                string reason = decision.Reason ?? "chat.rejected";
                TryLog(
                    OperationsLogLevel.Warning,
                    $"{Sanitize(context.Sender.Name)}: {Sanitize(content)} [rejected: {Sanitize(reason)}]");
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

        TryLog(
            OperationsLogLevel.Information,
            $"{Sanitize(context.Sender.Name)}: {Sanitize(content)}");
        return new ChatPipelineResult(
            true,
            changed,
            Array.AsReadOnly(deliveries));
    }

    private void TryLog(OperationsLogLevel level, string text)
    {
        try
        {
            logger?.Log(level, "chat", text);
        }
        catch
        {
            // Logging must never change chat delivery or moderation semantics.
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
