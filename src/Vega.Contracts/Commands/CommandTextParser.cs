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

        int separator = FindWhitespace(text);
        if (separator < 0)
        {
            command = new ParsedCommandText(text, Array.Empty<string>(), silent);
            return true;
        }

        string name = text[..separator];
        if (name.Length == 0)
        {
            return false;
        }

        command = new ParsedCommandText(
            name,
            ParseArguments(text[(separator + 1)..]),
            silent);
        return true;
    }

    private static IReadOnlyList<string> ParseArguments(string text)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];

            if (character == '\\' && ++index < text.Length)
            {
                char escaped = text[index];
                if (escaped is not ('"' or ' ' or '\\'))
                {
                    current.Append('\\');
                }

                current.Append(escaped);
            }
            else if (character == '"')
            {
                inQuotes = !inQuotes;
                if (!inQuotes)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
                else if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            }
            else if (IsWhitespace(character) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    private static int FindWhitespace(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (IsWhitespace(text[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsWhitespace(char character) =>
        character is ' ' or '\t' or '\n' or '\r';
}
