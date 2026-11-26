namespace Vega.Terraria.Worlds;

public sealed record NativeTileStorageOptions(bool Enabled, IReadOnlyList<string> ServerArguments)
{
    public static NativeTileStorageOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] input = arguments.ToArray();
        var nativeArguments = new List<string>(input.Length);
        bool enabled = false;
        const string option = "--vega-paged-tiles";
        for (int index = 0; index < input.Length; index++)
        {
            string argument = input[index];
            if (argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                enabled = ParseValue(argument[(option.Length + 1)..]);
            else if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= input.Length) throw new ArgumentException($"{option} requires 'on' or 'off'.");
                enabled = ParseValue(input[index]);
            }
            else nativeArguments.Add(argument);
        }
        return new NativeTileStorageOptions(enabled, Array.AsReadOnly(nativeArguments.ToArray()));
    }

    private static bool ParseValue(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => throw new ArgumentException("--vega-paged-tiles must be 'on' or 'off'.")
    };
}
