using System.Globalization;
using System.Text.Json;

namespace Vega.Core.Configuration;

public sealed record VegaServerConfiguration(
    string? WorldPath,
    int? Port,
    int? MaximumPlayers,
    string? Password,
    string? DefaultCulture,
    string? UiMode,
    bool? WorldImageEnabled);

public sealed record EffectiveConfigurationValue(string DisplayValue, string Source);

public sealed record EffectiveVegaServerConfiguration(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, EffectiveConfigurationValue> Values);

public sealed class VegaServerConfigurationState
{
    private readonly string path;
    private readonly string[] commandLine;
    private readonly Func<string, string?> environment;
    private EffectiveVegaServerConfiguration current;

    public VegaServerConfigurationState(
        string path,
        IEnumerable<string> commandLine,
        Func<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(commandLine);
        this.path = path;
        this.commandLine = commandLine.ToArray();
        this.environment = environment ?? Environment.GetEnvironmentVariable;
        current = VegaServerConfigurationStore.Compose(path, this.commandLine, this.environment);
    }

    public string Path => path;

    public EffectiveVegaServerConfiguration Current => Volatile.Read(ref current);

    public EffectiveVegaServerConfiguration Validate() =>
        VegaServerConfigurationStore.Compose(path, commandLine, environment);

    public EffectiveVegaServerConfiguration Reload()
    {
        EffectiveVegaServerConfiguration next = Validate();
        Volatile.Write(ref current, next);
        return next;
    }
}

public static class VegaServerConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static EffectiveVegaServerConfiguration Compose(
        string path,
        IReadOnlyList<string> commandLine,
        Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(commandLine);
        ArgumentNullException.ThrowIfNull(environment);

        VegaServerConfiguration file = LoadFile(path);
        var arguments = commandLine.ToList();
        var values = new Dictionary<string, EffectiveConfigurationValue>(StringComparer.OrdinalIgnoreCase);

        ApplyString(arguments, values, "world", "-world", "VEGA_WORLD", file.WorldPath, environment);
        ApplyInt(arguments, values, "port", "-port", "VEGA_PORT", file.Port, environment, 1, 65535);
        ApplyInt(arguments, values, "maxPlayers", "-maxplayers", "VEGA_MAX_PLAYERS", file.MaximumPlayers, environment, 1, 255);
        ApplyString(arguments, values, "password", "-password", "VEGA_PASSWORD", file.Password, environment, secret: true);
        ApplyCulture(arguments, values, file.DefaultCulture, environment);
        ApplyString(arguments, values, "ui", "--vega-ui", "VEGA_UI", file.UiMode, environment, equalsSyntax: true);
        ApplyBoolean(arguments, values, "worldImage", "--vega-world-image", "VEGA_WORLD_IMAGE", file.WorldImageEnabled, environment);

        return new EffectiveVegaServerConfiguration(
            Array.AsReadOnly(arguments.ToArray()),
            new Dictionary<string, EffectiveConfigurationValue>(values, StringComparer.OrdinalIgnoreCase));
    }

    private static VegaServerConfiguration LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new VegaServerConfiguration(null, null, null, null, null, null, null);
        }

        try
        {
            return JsonSerializer.Deserialize<VegaServerConfiguration>(
                File.ReadAllText(path),
                SerializerOptions) ?? throw new InvalidDataException(
                $"Server configuration '{System.IO.Path.GetFullPath(path)}' contains JSON null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Server configuration '{System.IO.Path.GetFullPath(path)}' is invalid JSON: {ex.Message}",
                ex);
        }
    }

    private static void ApplyString(
        List<string> arguments,
        Dictionary<string, EffectiveConfigurationValue> values,
        string name,
        string option,
        string environmentName,
        string? fileValue,
        Func<string, string?> environment,
        bool secret = false,
        bool equalsSyntax = false)
    {
        if (TryReadCommandLine(arguments, option, out string? commandLineValue))
        {
            values[name] = new EffectiveConfigurationValue(secret ? "<set>" : commandLineValue!, "command-line");
            return;
        }

        string? environmentValue = environment(environmentName);
        string? value = !string.IsNullOrWhiteSpace(environmentValue) ? environmentValue : fileValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            values[name] = new EffectiveConfigurationValue("<default>", "Terraria/default");
            return;
        }

        string source = !string.IsNullOrWhiteSpace(environmentValue) ? $"environment:{environmentName}" : "config/server.json";
        if (equalsSyntax)
        {
            arguments.Add($"{option}={value.Trim()}");
        }
        else
        {
            arguments.Add(option);
            arguments.Add(value.Trim());
        }

        values[name] = new EffectiveConfigurationValue(secret ? "<set>" : value.Trim(), source);
    }

    private static void ApplyInt(
        List<string> arguments,
        Dictionary<string, EffectiveConfigurationValue> values,
        string name,
        string option,
        string environmentName,
        int? fileValue,
        Func<string, string?> environment,
        int minimum,
        int maximum)
    {
        if (TryReadCommandLine(arguments, option, out string? commandLineValue))
        {
            int parsed = ParseInt(name, commandLineValue, minimum, maximum);
            values[name] = new EffectiveConfigurationValue(parsed.ToString(CultureInfo.InvariantCulture), "command-line");
            return;
        }

        string? rawEnvironment = environment(environmentName);
        int? value = !string.IsNullOrWhiteSpace(rawEnvironment)
            ? ParseInt(name, rawEnvironment, minimum, maximum)
            : fileValue;
        if (value is null)
        {
            values[name] = new EffectiveConfigurationValue("<default>", "Terraria/default");
            return;
        }

        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"Configuration property '{name}' must be between {minimum} and {maximum}; got {value}.");
        }

        arguments.Add(option);
        arguments.Add(value.Value.ToString(CultureInfo.InvariantCulture));
        values[name] = new EffectiveConfigurationValue(
            value.Value.ToString(CultureInfo.InvariantCulture),
            !string.IsNullOrWhiteSpace(rawEnvironment) ? $"environment:{environmentName}" : "config/server.json");
    }

    private static void ApplyCulture(
        List<string> arguments,
        Dictionary<string, EffectiveConfigurationValue> values,
        string? fileValue,
        Func<string, string?> environment)
    {
        string? rawEnvironment = environment("VEGA_DEFAULT_CULTURE");
        string? value = !string.IsNullOrWhiteSpace(rawEnvironment) ? rawEnvironment : fileValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            values["defaultCulture"] = new EffectiveConfigurationValue("<default>", "Terraria/default");
            return;
        }

        ValidateCulture(value);
        values["defaultCulture"] = new EffectiveConfigurationValue(
            value,
            !string.IsNullOrWhiteSpace(rawEnvironment) ? "environment:VEGA_DEFAULT_CULTURE" : "config/server.json");
    }

    private static void ApplyBoolean(
        List<string> arguments,
        Dictionary<string, EffectiveConfigurationValue> values,
        string name,
        string option,
        string environmentName,
        bool? fileValue,
        Func<string, string?> environment)
    {
        if (TryReadCommandLine(arguments, option, out string? commandLineValue))
        {
            bool parsed = ParseBoolean(name, commandLineValue);
            values[name] = new EffectiveConfigurationValue(parsed ? "on" : "off", "command-line");
            return;
        }

        string? rawEnvironment = environment(environmentName);
        bool? value = !string.IsNullOrWhiteSpace(rawEnvironment)
            ? ParseBoolean(name, rawEnvironment)
            : fileValue;
        if (value is null)
        {
            values[name] = new EffectiveConfigurationValue("off", "Vega/default");
            return;
        }

        arguments.Add($"{option}={(value.Value ? "on" : "off")}");
        values[name] = new EffectiveConfigurationValue(
            value.Value ? "on" : "off",
            !string.IsNullOrWhiteSpace(rawEnvironment) ? $"environment:{environmentName}" : "config/server.json");
    }

    private static bool TryReadCommandLine(
        IReadOnlyList<string> arguments,
        string option,
        out string? value)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = argument[(option.Length + 1)..];
                return true;
            }

            if (string.Equals(argument, option, StringComparison.OrdinalIgnoreCase) && index + 1 < arguments.Count)
            {
                value = arguments[index + 1];
                return true;
            }
        }

        value = null;
        return false;
    }

    private static int ParseInt(string name, string? value, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < minimum || parsed > maximum)
        {
            throw new InvalidDataException(
                $"Configuration property '{name}' must be an integer between {minimum} and {maximum}; got '{value}'.");
        }

        return parsed;
    }

    private static bool ParseBoolean(string name, string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "true" or "on" or "yes" => true,
        "0" or "false" or "off" or "no" => false,
        _ => throw new InvalidDataException(
            $"Configuration property '{name}' must be on/off or true/false; got '{value}'.")
    };

    private static void ValidateCulture(string value)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(value);
        }
        catch (CultureNotFoundException ex)
        {
            throw new InvalidDataException(
                $"Configuration property 'defaultCulture' contains unknown culture '{value}'.",
                ex);
        }
    }
}
