using System.Text.Json;

namespace Vega.Sandboxes;

/// <summary>Opt-in authenticated-character persistence and optional native SSC authority.</summary>
public sealed record PrimaryCharacterPersistenceOptions(bool Enabled = false, bool RequireLogin = false, bool RequireCharacterReady = false, bool Authoritative = false)
{
    public bool EffectiveRequireCharacterReady => RequireCharacterReady || Authoritative;
    public bool GameplayGateEnabled => RequireLogin || EffectiveRequireCharacterReady;

    public static PrimaryCharacterPersistenceOptions Load(string path)
    {
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Primary character persistence settings file is too large.");
        var options = JsonSerializer.Deserialize<PrimaryCharacterPersistenceOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Primary character persistence settings are empty.");
        if (options.EffectiveRequireCharacterReady && !options.Enabled)
            throw new InvalidDataException("RequireCharacterReady and Authoritative require primary character persistence to be enabled.");
        return options;
    }
}
