using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vega.MixedGameplay.Benchmarks;

internal static class BenchmarkReport
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    public static void Write(string path, object report) =>
        File.WriteAllText(path, ToNode(report)!.ToJsonString(Format));

    private static JsonNode? ToNode(object? value)
    {
        if (value is null) return null;
        Type type = value.GetType();
        if (type.Assembly != typeof(BenchmarkReport).Assembly)
            return JsonSerializer.SerializeToNode(value, type);

        // The serializer's reflection cache can outlive a collectible plugin.
        // Only framework values enter it. Report records are detached here.
        var node = new JsonObject();
        foreach (var property in type.GetProperties())
            node[property.Name] = ToNode(property.GetValue(value));
        return node;
    }
}
