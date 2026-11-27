namespace Vega.Terraria.Worlds;

public sealed record WorldImageOptions(
    bool Enabled,
    bool DirectImageRequested,
    IReadOnlyList<string> ServerArguments);

public static class WorldImageOptionsParser
{
    public static WorldImageOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] input = arguments.ToArray();
        var serverArguments = new List<string>(input.Length);
        bool? explicitlyEnabled = null;

        for (int index = 0; index < input.Length; index++)
        {
            string argument = input[index];
            if (argument.StartsWith("--vega-world-image=", StringComparison.OrdinalIgnoreCase))
            {
                explicitlyEnabled = ParseValue(argument[(argument.IndexOf('=') + 1)..]);
                continue;
            }

            if (string.Equals(argument, "--vega-world-image", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= input.Length)
                {
                    throw new ArgumentException("--vega-world-image requires 'on' or 'off'.");
                }

                explicitlyEnabled = ParseValue(input[index]);
                continue;
            }

            serverArguments.Add(argument);
        }

        bool directImageRequested = RewriteDirectImageSelection(serverArguments);
        if (directImageRequested && explicitlyEnabled is false)
        {
            throw new ArgumentException(
                "A .vworld file cannot be selected while --vega-world-image=off is set.");
        }

        return new WorldImageOptions(
            explicitlyEnabled ?? true,
            directImageRequested,
            Array.AsReadOnly(serverArguments.ToArray()));
    }

    private static bool RewriteDirectImageSelection(List<string> arguments)
    {
        bool selected = false;
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "-world", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < arguments.Count && IsWorldImage(arguments[index + 1]))
                {
                    arguments[index + 1] = Path.ChangeExtension(arguments[index + 1], ".wld");
                    selected = true;
                }

                continue;
            }

            const string worldPrefix = "-world=";
            if (argument.StartsWith(worldPrefix, StringComparison.OrdinalIgnoreCase) &&
                IsWorldImage(argument[worldPrefix.Length..]))
            {
                arguments[index] = worldPrefix +
                    Path.ChangeExtension(argument[worldPrefix.Length..], ".wld");
                selected = true;
            }
        }

        return selected;
    }

    private static bool IsWorldImage(string path) =>
        string.Equals(Path.GetExtension(path), ".vworld", StringComparison.OrdinalIgnoreCase);

    private static bool ParseValue(string value) => value.ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException("--vega-world-image must be 'on' or 'off'.")
    };
}
