using System.Text.Json;
using System.Text.Json.Serialization;
using Vega.Contracts.Operations;

namespace Vega.Core.Networking;

[method: JsonConstructor]
public sealed record GameplayEnforcementOptions(
    bool Enabled,
    bool RejectLivingGhost,
    bool RestrictSpectateToDeadPlayers,
    int MinimumDeathIntervalMilliseconds,
    int MaximumInteractionDistanceTiles,
    string BypassPermission,
    bool EnableDimensionsPacket,
    IReadOnlyList<string> DimensionsTrustedProxies)
{
    public GameplayEnforcementOptions(
        bool enabled,
        bool rejectLivingGhost,
        bool restrictSpectateToDeadPlayers,
        int minimumDeathIntervalMilliseconds,
        int maximumInteractionDistanceTiles,
        string bypassPermission)
        : this(
            enabled,
            rejectLivingGhost,
            restrictSpectateToDeadPlayers,
            minimumDeathIntervalMilliseconds,
            maximumInteractionDistanceTiles,
            bypassPermission,
            false,
            ["127.0.0.1"])
    {
    }

    public static GameplayEnforcementOptions Default { get; } = new(
        false,
        true,
        true,
        500,
        20,
        "vega.security.gameplay.bypass",
        false,
        ["127.0.0.1"]);

    public static GameplayEnforcementOptions Normalize(GameplayEnforcementOptions? candidate) =>
        new(
            candidate?.Enabled ?? Default.Enabled,
            candidate?.RejectLivingGhost ?? Default.RejectLivingGhost,
            candidate?.RestrictSpectateToDeadPlayers ?? Default.RestrictSpectateToDeadPlayers,
            Math.Clamp(
                candidate?.MinimumDeathIntervalMilliseconds ?? Default.MinimumDeathIntervalMilliseconds,
                0,
                60_000),
            Math.Clamp(
                candidate?.MaximumInteractionDistanceTiles ?? Default.MaximumInteractionDistanceTiles,
                0,
                200),
            string.IsNullOrWhiteSpace(candidate?.BypassPermission)
                ? Default.BypassPermission
                : candidate.BypassPermission.Trim(),
            candidate?.EnableDimensionsPacket ?? Default.EnableDimensionsPacket,
            Array.AsReadOnly((candidate?.DimensionsTrustedProxies ?? Default.DimensionsTrustedProxies)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()));
}

public static class GameplayEnforcementOptionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    public static GameplayEnforcementOptions Load(string path, IOperationsLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            return File.Exists(path)
                ? GameplayEnforcementOptions.Normalize(
                    JsonSerializer.Deserialize<GameplayEnforcementOptions>(
                        File.ReadAllText(path),
                        SerializerOptions))
                : GameplayEnforcementOptions.Default;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "gameplay-enforcement",
                $"Gameplay enforcement configuration '{Path.GetFullPath(path)}' is invalid; optional rules remain disabled.",
                ex);
            return GameplayEnforcementOptions.Default;
        }
    }
}
