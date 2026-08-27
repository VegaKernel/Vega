using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vega.PluginSdk;

/// <summary>
/// Explicit operator policy for exposing one registered plugin configuration through the
/// administration protocol. Managed plugin configurations remain hidden unless a matching rule
/// exists.
/// </summary>
public sealed record ManagedConfigurationExposureRule(
    string Plugin,
    string FileName,
    bool AllowEdit = true,
    IReadOnlyList<string>? RedactedJsonPointers = null,
    string? DisplayName = null,
    string? Description = null);

public sealed record ManagedConfigurationAdminDescriptor(
    string Plugin,
    string FileName,
    string Revision,
    bool CanEdit,
    bool HasRedactions,
    string? DisplayName = null,
    string? Description = null);

public sealed record ManagedConfigurationAdminDocument(
    ManagedConfigurationAdminDescriptor Descriptor,
    string Json);

public sealed record ManagedConfigurationAdminResult(
    bool Succeeded,
    ManagedConfigurationAdminDocument? Document = null,
    string? ErrorCode = null,
    string? Error = null)
{
    public static ManagedConfigurationAdminResult Failed(string errorCode, string error) =>
        new(false, ErrorCode: errorCode, Error: error);
}

/// <summary>
/// Production-safe facade over Vega's registered managed plugin configurations. It deliberately
/// does not expose filesystem paths, DEBUG capabilities or arbitrary files. Exposure is an
/// operator allowlist, writes use optimistic concurrency, and configured JSON Pointer values are
/// redacted on reads and restored from the authoritative document before typed validation/save.
/// </summary>
public sealed class ManagedConfigurationAdministration
{
    private const string RedactedValue = "[REDACTED]";
    private static readonly JsonSerializerOptions PolicyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private readonly IReadOnlyDictionary<(string Plugin, string FileName), ManagedConfigurationExposureRule> rules;

    public ManagedConfigurationAdministration(IEnumerable<ManagedConfigurationExposureRule>? rules = null)
    {
        var configured = new Dictionary<(string Plugin, string FileName), ManagedConfigurationExposureRule>(
            ConfigurationKeyComparer.Instance);
        foreach (ManagedConfigurationExposureRule rule in rules ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.Plugin);
            ArgumentException.ThrowIfNullOrWhiteSpace(rule.FileName);
            ValidatePointers(rule.RedactedJsonPointers);
            var key = (rule.Plugin.Trim(), NormalizeFileName(rule.FileName));
            if (!configured.TryAdd(key, rule with
                {
                    Plugin = key.Item1,
                    FileName = key.Item2,
                    RedactedJsonPointers = rule.RedactedJsonPointers?.ToArray() ?? []
                }))
            {
                throw new InvalidOperationException(
                    $"Managed configuration administration rule '{key.Item1}/{key.Item2}' is duplicated.");
            }
        }

        this.rules = configured;
    }

    /// <summary>
    /// Loads an operator exposure policy. Invalid or inaccessible policy files fail closed and
    /// return a facade exposing no managed configuration documents.
    /// </summary>
    public static ManagedConfigurationAdministration LoadPolicy(
        string path,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            error = null;
            return new ManagedConfigurationAdministration();
        }

        try
        {
            AdminConfigurationExposureFile? file = JsonSerializer.Deserialize<AdminConfigurationExposureFile>(
                File.ReadAllText(path),
                PolicyJsonOptions);
            error = null;
            return new ManagedConfigurationAdministration(file?.Documents ?? []);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            error = ex.Message;
            return new ManagedConfigurationAdministration();
        }
    }

    public IReadOnlyList<ManagedConfigurationAdminDescriptor> Snapshot()
    {
        VegaDebugPluginConfigurationSnapshot[] registered = VegaManagedConfigurationDiagnostics
            .Snapshot()
            .ToArray();
        var result = new List<ManagedConfigurationAdminDescriptor>();
        foreach (VegaDebugPluginConfigurationSnapshot configuration in registered)
        {
            if (!TryGetRule(configuration.Plugin, configuration.FileName, out ManagedConfigurationExposureRule? rule))
            {
                continue;
            }

            result.Add(CreateDescriptor(rule!, configuration.Json));
        }

        return Array.AsReadOnly(result
            .OrderBy(static descriptor => descriptor.Plugin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static descriptor => descriptor.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public ManagedConfigurationAdminResult Read(string plugin, string fileName)
    {
        if (!TryGetRule(plugin, fileName, out ManagedConfigurationExposureRule? rule))
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.not_exposed",
                "The requested managed configuration is not exposed to administration.");
        }

        VegaDebugPluginConfigurationResult current = VegaManagedConfigurationDiagnostics.Read(
            rule!.Plugin,
            rule.FileName);
        if (!current.Succeeded || current.Json is null)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.read_failed",
                current.Error ?? "The managed configuration could not be read.");
        }

        return BuildDocument(rule, current.Json);
    }

    public ManagedConfigurationAdminResult Replace(
        string plugin,
        string fileName,
        string expectedRevision,
        string json)
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        ArgumentNullException.ThrowIfNull(json);
        if (!TryGetRule(plugin, fileName, out ManagedConfigurationExposureRule? rule))
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.not_exposed",
                "The requested managed configuration is not exposed to administration.");
        }

        if (!rule!.AllowEdit)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.read_only",
                "The requested managed configuration is read-only for administration.");
        }

        VegaDebugPluginConfigurationResult current = VegaManagedConfigurationDiagnostics.Read(
            rule.Plugin,
            rule.FileName);
        if (!current.Succeeded || current.Json is null)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.read_failed",
                current.Error ?? "The managed configuration could not be read.");
        }

        string currentRevision = ComputeRevision(current.Json);
        if (!string.Equals(currentRevision, expectedRevision, StringComparison.Ordinal))
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.revision_conflict",
                "The managed configuration changed after it was opened. Reload it before saving.");
        }

        string candidate;
        try
        {
            candidate = RestoreRedactions(current.Json, json, rule.RedactedJsonPointers ?? []);
        }
        catch (JsonException ex)
        {
            return ManagedConfigurationAdminResult.Failed("configuration.invalid_json", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ManagedConfigurationAdminResult.Failed("configuration.redaction_conflict", ex.Message);
        }

        VegaDebugPluginConfigurationResult replaced = VegaManagedConfigurationDiagnostics.Replace(
            rule.Plugin,
            rule.FileName,
            candidate);
        if (!replaced.Succeeded || replaced.Json is null)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.validation_failed",
                replaced.Error ?? "The managed configuration was rejected by its typed validator.");
        }

        return BuildDocument(rule, replaced.Json);
    }

    public ManagedConfigurationAdminResult Reload(
        string plugin,
        string fileName,
        string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(expectedRevision);
        if (!TryGetRule(plugin, fileName, out ManagedConfigurationExposureRule? rule))
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.not_exposed",
                "The requested managed configuration is not exposed to administration.");
        }

        if (!rule!.AllowEdit)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.read_only",
                "The requested managed configuration is read-only for administration.");
        }

        VegaDebugPluginConfigurationResult current = VegaManagedConfigurationDiagnostics.Read(
            rule.Plugin,
            rule.FileName);
        if (!current.Succeeded || current.Json is null)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.read_failed",
                current.Error ?? "The managed configuration could not be read.");
        }

        if (!string.Equals(ComputeRevision(current.Json), expectedRevision, StringComparison.Ordinal))
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.revision_conflict",
                "The managed configuration changed after it was opened. Refresh it before reloading.");
        }

        VegaDebugPluginConfigurationResult reloaded = VegaManagedConfigurationDiagnostics.Reload(
            rule.Plugin,
            rule.FileName);
        if (!reloaded.Succeeded || reloaded.Json is null)
        {
            return ManagedConfigurationAdminResult.Failed(
                "configuration.reload_failed",
                reloaded.Error ?? "The managed configuration could not be reloaded.");
        }

        return BuildDocument(rule, reloaded.Json);
    }

    private ManagedConfigurationAdminResult BuildDocument(
        ManagedConfigurationExposureRule rule,
        string rawJson)
    {
        try
        {
            string visibleJson = Redact(rawJson, rule.RedactedJsonPointers ?? []);
            return new ManagedConfigurationAdminResult(
                true,
                new ManagedConfigurationAdminDocument(CreateDescriptor(rule, rawJson), visibleJson));
        }
        catch (JsonException ex)
        {
            return ManagedConfigurationAdminResult.Failed("configuration.invalid_json", ex.Message);
        }
    }

    private static ManagedConfigurationAdminDescriptor CreateDescriptor(
        ManagedConfigurationExposureRule rule,
        string rawJson) =>
        new(
            rule.Plugin,
            rule.FileName,
            ComputeRevision(rawJson),
            rule.AllowEdit,
            (rule.RedactedJsonPointers?.Count ?? 0) > 0,
            rule.DisplayName,
            rule.Description);

    private bool TryGetRule(
        string plugin,
        string fileName,
        out ManagedConfigurationExposureRule? rule)
    {
        if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(fileName))
        {
            rule = null;
            return false;
        }

        return rules.TryGetValue((plugin.Trim(), NormalizeFileName(fileName)), out rule);
    }

    private static string Redact(string rawJson, IReadOnlyList<string> pointers)
    {
        JsonNode? root = JsonNode.Parse(rawJson) ?? throw new JsonException("Managed configuration contains null JSON.");
        JsonNode visible = root.DeepClone();
        foreach (string pointer in pointers)
        {
            if (TryResolveParent(visible, pointer, out JsonNode? parent, out string token) && parent is not null)
            {
                SetChild(parent, token, JsonValue.Create(RedactedValue));
            }
        }

        return visible.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RestoreRedactions(
        string currentJson,
        string submittedJson,
        IReadOnlyList<string> pointers)
    {
        JsonNode? current = JsonNode.Parse(currentJson) ?? throw new JsonException("Managed configuration contains null JSON.");
        JsonNode? submitted = JsonNode.Parse(submittedJson) ?? throw new JsonException("Submitted configuration contains null JSON.");
        foreach (string pointer in pointers)
        {
            if (!TryResolveParent(current, pointer, out JsonNode? currentParent, out string token) || currentParent is null)
            {
                continue;
            }

            JsonNode? secret = GetChild(currentParent, token)?.DeepClone();
            if (!TryResolveParent(submitted, pointer, out JsonNode? submittedParent, out string submittedToken) || submittedParent is null)
            {
                throw new InvalidOperationException(
                    $"The submitted document removes the container for redacted value '{pointer}'.");
            }

            SetChild(submittedParent, submittedToken, secret);
        }

        return submitted.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static bool TryResolveParent(
        JsonNode root,
        string pointer,
        out JsonNode? parent,
        out string token)
    {
        string[] tokens = ParsePointer(pointer);
        if (tokens.Length == 0)
        {
            parent = null;
            token = string.Empty;
            return false;
        }

        JsonNode? current = root;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            current = current is null ? null : GetChild(current, tokens[index]);
            if (current is null)
            {
                parent = null;
                token = tokens[^1];
                return false;
            }
        }

        parent = current;
        token = tokens[^1];
        return parent is not null;
    }

    private static JsonNode? GetChild(JsonNode parent, string token) => parent switch
    {
        JsonObject objectNode => objectNode[token],
        JsonArray arrayNode when int.TryParse(token, out int index) && index >= 0 && index < arrayNode.Count => arrayNode[index],
        _ => null
    };

    private static void SetChild(JsonNode parent, string token, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject objectNode:
                if (!objectNode.ContainsKey(token))
                {
                    throw new InvalidOperationException($"Redacted JSON property '{token}' is missing from the submitted document.");
                }

                objectNode[token] = value;
                break;
            case JsonArray arrayNode when int.TryParse(token, out int index) && index >= 0 && index < arrayNode.Count:
                arrayNode[index] = value;
                break;
            default:
                throw new InvalidOperationException($"Redacted JSON pointer token '{token}' cannot be restored.");
        }
    }

    private static string[] ParsePointer(string pointer)
    {
        if (pointer.Length == 0)
        {
            return [];
        }

        if (pointer[0] != '/')
        {
            throw new ArgumentException($"JSON Pointer '{pointer}' must start with '/'.", nameof(pointer));
        }

        return pointer[1..]
            .Split('/')
            .Select(static token => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))
            .ToArray();
    }

    private static void ValidatePointers(IReadOnlyList<string>? pointers)
    {
        foreach (string pointer in pointers ?? [])
        {
            ArgumentNullException.ThrowIfNull(pointer);
            _ = ParsePointer(pointer);
            if (pointer.Length == 0)
            {
                throw new ArgumentException("Redacting the whole configuration document is not supported.", nameof(pointers));
            }
        }
    }

    private static string ComputeRevision(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();

    private static string NormalizeFileName(string fileName) =>
        fileName.Trim().Replace('\\', '/');

    private sealed record AdminConfigurationExposureFile(
        IReadOnlyList<ManagedConfigurationExposureRule>? Documents = null);

    private sealed class ConfigurationKeyComparer : IEqualityComparer<(string Plugin, string FileName)>
    {
        public static ConfigurationKeyComparer Instance { get; } = new();

        public bool Equals(
            (string Plugin, string FileName) x,
            (string Plugin, string FileName) y) =>
            string.Equals(x.Plugin, y.Plugin, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.FileName, y.FileName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Plugin, string FileName) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Plugin),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.FileName));
    }
}
