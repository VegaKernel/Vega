using System.Reflection;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Players;

namespace Vega.Modules.Mentions;

public sealed class MentionRenderer : IVegaModule, IChatRecipientRenderer
{
    public VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Modules.Mentions",
        typeof(MentionRenderer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        ["Vega.Contracts"]);

    public int Order => 100;

    public void Start()
    {
    }

    public ChatRecipientRenderResult Render(
        ChatMessageContext context,
        PlayerSnapshot recipient,
        string content)
    {
        string token = $"@{recipient.Name}";
        int index = FindMention(content, token, 0);
        if (index < 0)
        {
            return new ChatRecipientRenderResult(content);
        }

        var rendered = new System.Text.StringBuilder(content.Length + 16);
        int consumed = 0;
        do
        {
            rendered.Append(content, consumed, index - consumed)
                .Append("[MENTION ")
                .Append(content, index, token.Length)
                .Append(']');
            consumed = index + token.Length;
            index = FindMention(content, token, consumed);
        }
        while (index >= 0);

        rendered.Append(content, consumed, content.Length - consumed);
        return new ChatRecipientRenderResult(rendered.ToString(), true);
    }

    public void Dispose()
    {
    }

    private static int FindMention(string content, string token, int start)
    {
        int index = content.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            int end = index + token.Length;
            bool leftBoundary = index == 0 || !IsNameCharacter(content[index - 1]);
            bool rightBoundary = end == content.Length || !IsNameCharacter(content[end]);
            if (leftBoundary && rightBoundary)
            {
                return index;
            }

            index = content.IndexOf(token, index + token.Length, StringComparison.OrdinalIgnoreCase);
        }

        return -1;
    }

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
