namespace TShockNG.Contracts.Commands;

/// <summary>
/// Parsed command text independent from a concrete console or chat transport.
/// </summary>
public readonly record struct ParsedCommandText(
    string Name,
    IReadOnlyList<string> Arguments,
    bool Silent);

public static class CommandTextParser
{
    public static bool TryParse(string? commandText, out ParsedCommandText command)
    {
        command = default;

        if (string.IsNullOrWhiteSpace(commandText))
        {
            return false;
        }

        var text = commandText.Trim();
        var silent = false;

        if (text[0] is '/' or '.')
        {
            silent = text[0] == '.';
            text = text[1..].TrimStart();
        }

        if (text.Length == 0)
        {
            return false;
        }

        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator < 0)
        {
            command = new ParsedCommandText(text, Array.Empty<string>(), silent);
            return true;
        }

        var name = text[..separator];
        if (name.Length == 0)
        {
            return false;
        }

        var arguments = text[(separator + 1)..]
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        command = new ParsedCommandText(name, arguments, silent);
        return true;
    }
}
