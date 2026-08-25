using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vega.PluginSdk;

public sealed record VegaDebugPluginConfigurationSnapshot(
    string Plugin,
    string FileName,
    string Path,
    string Json);

public sealed record VegaDebugPluginConfigurationResult(
    bool Succeeded,
    string? Json = null,
    string? Error = null);

/// <summary>DEBUG-only helpers for inspecting and safely modifying registered plugin configuration.</summary>
public static class VegaDebugConfigurationExtensions
{
    public static IReadOnlyList<VegaDebugPluginConfigurationSnapshot> SnapshotPluginConfigurations(
        this IVegaDebugContext debug)
    {
        EnsureRead(debug);
        return VegaManagedConfigurationDiagnostics.Snapshot();
    }

    public static VegaDebugPluginConfigurationResult ReadPluginConfiguration(
        this IVegaDebugContext debug,
        string plugin,
        string fileName)
    {
        EnsureRead(debug);
        return VegaManagedConfigurationDiagnostics.Read(plugin, fileName);
    }

    /// <summary>
    /// Applies a JSON merge-patch, validates the resulting value through the owning plugin's typed
    /// validator and atomically persists it only when validation succeeds.
    /// </summary>
    public static VegaDebugPluginConfigurationResult PatchPluginConfiguration(
        this IVegaDebugContext debug,
        string plugin,
        string fileName,
        JsonElement patch)
    {
        EnsureWrite(debug);
        return VegaManagedConfigurationDiagnostics.Patch(plugin, fileName, patch);
    }

    public static VegaDebugPluginConfigurationResult ReplacePluginConfiguration(
        this IVegaDebugContext debug,
        string plugin,
        string fileName,
        string json)
    {
        EnsureWrite(debug);
        return VegaManagedConfigurationDiagnostics.Replace(plugin, fileName, json);
    }

    public static VegaDebugPluginConfigurationResult ReloadPluginConfiguration(
        this IVegaDebugContext debug,
        string plugin,
        string fileName)
    {
        EnsureWrite(debug);
        return VegaManagedConfigurationDiagnostics.Reload(plugin, fileName);
    }

    private static void EnsureRead(IVegaDebugContext debug)
    {
        ArgumentNullException.ThrowIfNull(debug);
        if (debug.Capabilities == VegaDebugCapabilities.None)
        {
            throw new InvalidOperationException("The Vega DEBUG API is disabled or this plugin is not trusted.");
        }
    }

    private static void EnsureWrite(IVegaDebugContext debug)
    {
        EnsureRead(debug);
        if ((debug.Capabilities & VegaDebugCapabilities.StateMutation) == 0)
        {
            throw new InvalidOperationException(
                "Plugin configuration mutation requires the DEBUG StateMutation capability.");
        }
    }
}

internal static class VegaManagedConfigurationDiagnostics
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(string Plugin, string FileName), Entry> Entries = new();

    internal static IDisposable Register(
        string plugin,
        string fileName,
        string path,
        Func<string> read,
        Func<string, string?> validate,
        Action<string> replace,
        Action reload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(reload);

        var key = (plugin.Trim(), fileName.Trim());
        var entry = new Entry(key.Item1, key.Item2, Path.GetFullPath(path), read, validate, replace, reload);
        lock (Sync)
        {
            Entries[key] = entry;
        }

        return new Registration(key, entry);
    }

    internal static IReadOnlyList<VegaDebugPluginConfigurationSnapshot> Snapshot()
    {
        Entry[] entries;
        lock (Sync)
        {
            entries = Entries.Values
                .OrderBy(static entry => entry.Plugin, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var result = new List<VegaDebugPluginConfigurationSnapshot>(entries.Length);
        foreach (Entry entry in entries)
        {
            try
            {
                result.Add(new VegaDebugPluginConfigurationSnapshot(
                    entry.Plugin,
                    entry.FileName,
                    entry.Path,
                    entry.Read()));
            }
            catch (Exception ex)
            {
                var error = new JsonObject { ["error"] = ex.Message };
                result.Add(new VegaDebugPluginConfigurationSnapshot(
                    entry.Plugin,
                    entry.FileName,
                    entry.Path,
                    error.ToJsonString()));
            }
        }

        return Array.AsReadOnly(result.ToArray());
    }

    internal static VegaDebugPluginConfigurationResult Read(string plugin, string fileName)
    {
        if (!TryGet(plugin, fileName, out Entry? entry, out VegaDebugPluginConfigurationResult? failure))
        {
            return failure!;
        }

        try
        {
            return new VegaDebugPluginConfigurationResult(true, entry!.Read());
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }

    internal static VegaDebugPluginConfigurationResult Replace(string plugin, string fileName, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (!TryGet(plugin, fileName, out Entry? entry, out VegaDebugPluginConfigurationResult? failure))
        {
            return failure!;
        }

        try
        {
            string? validation = entry!.Validate(json);
            if (!string.IsNullOrWhiteSpace(validation))
            {
                return Failed(validation);
            }

            entry.Replace(json);
            return new VegaDebugPluginConfigurationResult(true, entry.Read());
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }

    internal static VegaDebugPluginConfigurationResult Patch(
        string plugin,
        string fileName,
        JsonElement patch)
    {
        VegaDebugPluginConfigurationResult current = Read(plugin, fileName);
        if (!current.Succeeded || current.Json is null)
        {
            return current;
        }

        try
        {
            JsonNode? target = JsonNode.Parse(current.Json);
            JsonNode? patchNode = JsonNode.Parse(patch.GetRawText());
            JsonNode? merged = Merge(target, patchNode);
            string json = merged?.ToJsonString() ?? "null";
            return Replace(plugin, fileName, json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }
    }

    internal static VegaDebugPluginConfigurationResult Reload(string plugin, string fileName)
    {
        if (!TryGet(plugin, fileName, out Entry? entry, out VegaDebugPluginConfigurationResult? failure))
        {
            return failure!;
        }

        try
        {
            entry!.Reload();
            return new VegaDebugPluginConfigurationResult(true, entry.Read());
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }

    private static JsonNode? Merge(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        JsonObject result = target is JsonObject targetObject
            ? (JsonObject)targetObject.DeepClone()
            : new JsonObject();
        foreach ((string key, JsonNode? patchValue) in patchObject)
        {
            if (patchValue is null)
            {
                result.Remove(key);
                continue;
            }

            result[key] = Merge(result[key], patchValue);
        }

        return result;
    }

    private static bool TryGet(
        string plugin,
        string fileName,
        out Entry? entry,
        out VegaDebugPluginConfigurationResult? failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        lock (Sync)
        {
            Entries.TryGetValue((plugin.Trim(), fileName.Trim()), out entry);
        }

        if (entry is not null)
        {
            failure = null;
            return true;
        }

        failure = Failed($"Registered plugin configuration '{plugin}/{fileName}' was not found.");
        return false;
    }

    private static VegaDebugPluginConfigurationResult Failed(string error) => new(false, Error: error);

    private sealed record Entry(
        string Plugin,
        string FileName,
        string Path,
        Func<string> Read,
        Func<string, string?> Validate,
        Action<string> Replace,
        Action Reload);

    private sealed class Registration((string Plugin, string FileName) key, Entry entry) : IDisposable
    {
        private Entry? entry = entry;

        public void Dispose()
        {
            Entry? current = Interlocked.Exchange(ref entry, null);
            if (current is null)
            {
                return;
            }

            lock (Sync)
            {
                if (Entries.TryGetValue(key, out Entry? registered) && ReferenceEquals(registered, current))
                {
                    Entries.Remove(key);
                }
            }
        }
    }
}
