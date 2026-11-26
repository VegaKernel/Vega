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
        string name = recipient.Name ?? string.Empty;
        int tokenLength = name.Length + 1;
        int index = FindMention(content, name, 0);
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
                .Append(content, index, tokenLength)
                .Append(']');
            consumed = index + tokenLength;
            index = FindMention(content, name, consumed);
        }
        while (index >= 0);

        rendered.Append(content, consumed, content.Length - consumed);
        return new ChatRecipientRenderResult(rendered.ToString(), true);
    }

    public void Dispose()
    {
    }

    private static int FindMention(string content, string name, int start)
    {
        int index = content.IndexOf('@', start);
        while (index >= 0)
        {
            int end = index + 1 + name.Length;
            if (end > content.Length)
                return -1;
            if (!content.AsSpan(index + 1, name.Length).Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                index = content.IndexOf('@', index + 1);
                continue;
            }
            bool leftBoundary = index == 0 || !IsNameCharacter(content[index - 1]);
            bool rightBoundary = end == content.Length || !IsNameCharacter(content[end]);
            if (leftBoundary && rightBoundary)
            {
                return index;
            }

            index = content.IndexOf('@', end);
        }

        return -1;
    }

    private static bool IsNameCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';
}
