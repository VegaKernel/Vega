namespace Vega.Terraria.Worlds;

public sealed record WorldImageOptions(bool Enabled, IReadOnlyList<string> ServerArguments);

public static class WorldImageOptionsParser
{
    public static WorldImageOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] input = arguments.ToArray();
        var serverArguments = new List<string>(input.Length);
        bool enabled = false;

        for (int index = 0; index < input.Length; index++)
        {
            string argument = input[index];
            if (argument.StartsWith("--vega-world-image=", StringComparison.OrdinalIgnoreCase))
            {
                enabled = ParseValue(argument[(argument.IndexOf('=') + 1)..]);
                continue;
            }

            if (string.Equals(argument, "--vega-world-image", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= input.Length)
                {
                    throw new ArgumentException("--vega-world-image requires 'on' or 'off'.");
                }

                enabled = ParseValue(input[index]);
                continue;
            }

            serverArguments.Add(argument);
        }

        return new WorldImageOptions(enabled, Array.AsReadOnly(serverArguments.ToArray()));
    }

    private static bool ParseValue(string value) => value.ToLowerInvariant() switch
    {
        "on" => true,
        "off" => false,
        _ => throw new ArgumentException("--vega-world-image must be 'on' or 'off'.")
    };
}
