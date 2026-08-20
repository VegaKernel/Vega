using System.Globalization;

namespace Vega.Contracts.Messaging;

/// <summary>
/// Formats a structured message when no localization catalog has resolved it yet.
/// </summary>
public static class MessageFallbackFormatter
{
    public static string Format(MessageDescriptor message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.DefaultText is not null)
        {
            try
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    message.DefaultText,
                    message.Arguments.ToArray());
            }
            catch (FormatException)
            {
                // Fall back to the stable key instead of letting a bad source template break command delivery.
            }
        }

        return message.Arguments.Count == 0
            ? message.Key
            : $"{message.Key}: {string.Join(", ", message.Arguments.Select(static value => value?.ToString() ?? "<null>"))}";
    }
}
