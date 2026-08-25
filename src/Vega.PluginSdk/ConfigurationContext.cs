namespace Vega.PluginSdk;

/// <summary>
/// Managed JSON configuration owned by a plugin. Configuration files live under the plugin's
/// dedicated configuration directory and are loaded, validated and replaced atomically by Vega.
/// </summary>
public interface IPluginConfiguration
{
    string Directory { get; }

    IPluginConfig<T> Use<T>(
        string fileName,
        Func<T> defaults,
        Func<T, string?>? validate = null);
}

/// <summary>A strongly typed configuration file managed for the lifetime of a plugin.</summary>
public interface IPluginConfig<T>
{
    string FileName { get; }

    T Current { get; }

    /// <summary>Re-reads the file from disk, validates it and updates <see cref="Current"/>.</summary>
    T Reload();

    /// <summary>Atomically writes the supplied value and makes it current.</summary>
    void Save(T value);

    /// <summary>Atomically persists the current value.</summary>
    void Save();
}
