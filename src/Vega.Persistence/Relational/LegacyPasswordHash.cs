using System.Globalization;
using System.Text;

namespace Vega.Persistence.Relational;

/// <summary>Bounded compatibility with the BCrypt format used by the supported TShock migration source.</summary>
public static class LegacyPasswordHash
{
    public static bool IsSupported(string? encoded) => encoded is { Length: 60 } &&
        (encoded.StartsWith("$2a$", StringComparison.Ordinal) || encoded.StartsWith("$2b$", StringComparison.Ordinal) ||
         encoded.StartsWith("$2y$", StringComparison.Ordinal)) && encoded[6] == '$' &&
        int.TryParse(encoded.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int cost) &&
        cost is >= 4 and <= 14 && encoded.AsSpan(7).IndexOfAnyExcept("./ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789") < 0;

    internal static bool Verify(string password, string encoded)
    {
        if (!IsSupported(encoded) || string.IsNullOrEmpty(password) || Encoding.UTF8.GetByteCount(password) > 72)
            return false;
        try { return BCrypt.Net.BCrypt.Verify(password, encoded); }
        catch (BCrypt.Net.SaltParseException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
