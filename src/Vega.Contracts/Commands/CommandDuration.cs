using System.Globalization;
using System.Text.RegularExpressions;

namespace Vega.Contracts.Commands;

/// <summary>Parses TShock-style units, including combinations such as 1d2h and 10h-30m+2m.</summary>
public static class CommandDuration
{
    private static readonly Regex Part = new(@"\G\s*(?<amount>[+-]?\d+(?:\.\d+)?)\s*(?<unit>[smhdw])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string value, TimeSpan maximum, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        value = value.Trim();
        int consumed = 0;
        double seconds = 0;
        foreach (Match part in Part.Matches(value))
        {
            if (!double.TryParse(part.Groups["amount"].Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double amount)) return false;
            seconds += amount * (char.ToLowerInvariant(part.Groups["unit"].Value[0]) switch
            {
                's' => 1, 'm' => 60, 'h' => 3600, 'd' => 86400, 'w' => 604800, _ => 0
            });
            consumed = part.Index + part.Length;
        }
        if (consumed != value.Length || !double.IsFinite(seconds) || seconds <= 0 || seconds > maximum.TotalSeconds) return false;
        duration = TimeSpan.FromSeconds(seconds);
        return duration > TimeSpan.Zero;
    }
}
