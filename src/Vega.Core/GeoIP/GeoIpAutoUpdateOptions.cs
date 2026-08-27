using System.Text.Json;

namespace Vega.Core.GeoIP;

public sealed record GeoIpAutoUpdateOptions(bool AutoUpdate)
{
    public static GeoIpAutoUpdateOptions Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return new GeoIpAutoUpdateOptions(false);
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Server configuration root must be a JSON object.");
            }

            if (!TryGetProperty(root, "GeoIP", out JsonElement geoIp))
            {
                return new GeoIpAutoUpdateOptions(false);
            }

            if (geoIp.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("GeoIP configuration must be a JSON object.");
            }

            if (!TryGetProperty(geoIp, "AutoUpdate", out JsonElement autoUpdate))
            {
                return new GeoIpAutoUpdateOptions(false);
            }

            return autoUpdate.ValueKind switch
            {
                JsonValueKind.True => new GeoIpAutoUpdateOptions(true),
                JsonValueKind.False => new GeoIpAutoUpdateOptions(false),
                _ => throw new InvalidDataException("GeoIP.AutoUpdate must be a boolean.")
            };
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Server configuration JSON is invalid.", ex);
        }
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
