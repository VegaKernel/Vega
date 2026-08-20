namespace Vega.TerminalUI;

public enum OperationsUiMode
{
    Auto,
    Terminal,
    Plain,
    Headless
}

public sealed record OperationsUiOptions(
    OperationsUiMode RequestedMode,
    OperationsUiMode EffectiveMode,
    IReadOnlyList<string> ServerArguments);

public static class OperationsUiModeParser
{
    private const string OptionName = "--vega-ui";

    public static OperationsUiOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] source = arguments.ToArray();
        var serverArguments = new List<string>(source.Length);
        OperationsUiMode mode = OperationsUiMode.Auto;

        for (int index = 0; index < source.Length; index++)
        {
            string argument = source[index];
            string? value = null;

            if (argument.StartsWith(OptionName + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(OptionName.Length + 1)..];
            }
            else if (string.Equals(argument, OptionName, StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= source.Length)
                {
                    throw new ArgumentException($"{OptionName} requires auto, tui, plain, or headless.");
                }

                value = source[index];
            }
            else
            {
                serverArguments.Add(argument);
                continue;
            }

            mode = ParseMode(value);
        }

        OperationsUiMode effective = mode == OperationsUiMode.Auto
            ? SelectAutomaticMode()
            : mode;

        return new OperationsUiOptions(
            mode,
            effective,
            Array.AsReadOnly(serverArguments.ToArray()));
    }

    private static OperationsUiMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "auto" => OperationsUiMode.Auto,
        "tui" or "terminal" => OperationsUiMode.Terminal,
        "plain" or "console" => OperationsUiMode.Plain,
        "headless" => OperationsUiMode.Headless,
        _ => throw new ArgumentException($"Unknown {OptionName} mode '{value}'.")
    };

    private static OperationsUiMode SelectAutomaticMode() =>
        Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected
            ? OperationsUiMode.Terminal
            : OperationsUiMode.Plain;
}
