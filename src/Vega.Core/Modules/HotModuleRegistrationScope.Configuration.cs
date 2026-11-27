using System.Text.Json;
using Vega.Contracts.Operations;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginConfiguration(
        HotModuleRegistrationScope owner,
        string pluginName,
        string directory) : IPluginConfiguration
    {
        private readonly object sync = new();
        private readonly Dictionary<string, object> configs = new(StringComparer.OrdinalIgnoreCase);

        public string Directory { get; } = Path.GetFullPath(directory);

        public IPluginConfig<T> Use<T>(
            string fileName,
            Func<T> defaults,
            Func<T, string?>? validate = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            ArgumentNullException.ThrowIfNull(defaults);
            string path = Resolve(fileName);
            string logicalName = fileName.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            lock (sync)
            {
                if (configs.TryGetValue(path, out object? existing))
                {
                    return existing as IPluginConfig<T> ?? throw new InvalidOperationException(
                        $"Plugin configuration '{fileName}' is already registered with another value type.");
                }

                var config = new PluginConfig<T>(logicalName, path, defaults, validate);
                configs.Add(path, config);
                owner.Track(VegaManagedConfigurationDiagnostics.Register(
                    pluginName,
                    logicalName,
                    path,
                    () => InvokeDiagnostic($"config:{logicalName}:read", config.ReadJson),
                    json => InvokeDiagnostic($"config:{logicalName}:validate", () => config.ValidateJson(json)),
                    json => InvokeDiagnostic($"config:{logicalName}:replace", () => { config.ReplaceJson(json); return true; }),
                    () => InvokeDiagnostic($"config:{logicalName}:reload", () => { config.ReloadUntyped(); return true; })));
                return config;
            }
        }

        private TResult InvokeDiagnostic<TResult>(string operation, Func<TResult> callback)
        {
            TResult result = default!;
            Exception? expected = null;
            bool succeeded = owner.executionGuard.TryInvoke(operation, () =>
            {
                try { result = callback(); }
                // Invalid operator input or storage failure is not a plugin callback fault.
                catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or NotSupportedException)
                { expected = ex; }
            });
            if (!succeeded) throw new InvalidDataException("Plugin configuration callback failed or is quarantined.");
            if (expected is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(expected).Throw();
            return result;
        }

        private string Resolve(string fileName)
        {
            if (Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("Plugin configuration paths must be relative.", nameof(fileName));
            }

            string path = Path.GetFullPath(Path.Combine(Directory, fileName));
            string prefix = Directory.EndsWith(Path.DirectorySeparatorChar)
                ? Directory
                : Directory + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Plugin configuration paths cannot leave the plugin configuration directory.", nameof(fileName));
            }

            return path;
        }
    }

    private sealed class PluginConfig<T> : IPluginConfig<T>
    {
        private readonly object sync = new();
        private readonly string path;
        private readonly Func<T> defaults;
        private readonly Func<T, string?>? validate;
        private T current;

        public PluginConfig(string fileName, string path, Func<T> defaults, Func<T, string?>? validate)
        {
            FileName = fileName;
            this.path = path;
            this.defaults = defaults;
            this.validate = validate;
            current = Load(writeDefaults: true);
        }

        public string FileName { get; }

        public T Current
        {
            get
            {
                lock (sync)
                {
                    return current;
                }
            }
        }

        public T Reload()
        {
            lock (sync)
            {
                current = Load(writeDefaults: false);
                return current;
            }
        }

        public void Save(T value)
        {
            lock (sync)
            {
                Validate(value);
                WriteAtomic(value);
                current = value;
            }
        }

        public void Save()
        {
            lock (sync)
            {
                Validate(current);
                WriteAtomic(current);
            }
        }

        public string ReadJson()
        {
            lock (sync)
            {
                return JsonSerializer.Serialize(current, JsonOptions);
            }
        }

        public string? ValidateJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            try
            {
                T value = JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                    throw new InvalidDataException($"Plugin configuration '{path}' contains null.");
                Validate(value);
                return null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
            {
                return ex.Message;
            }
        }

        public void ReplaceJson(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            T value = JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                throw new InvalidDataException($"Plugin configuration '{path}' contains null.");
            Save(value);
        }

        public void ReloadUntyped() => Reload();

        private T Load(bool writeDefaults)
        {
            T value;
            if (File.Exists(path))
            {
                value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) ??
                    throw new InvalidDataException($"Plugin configuration '{path}' contains null.");
            }
            else
            {
                value = defaults();
                Validate(value);
                if (writeDefaults)
                {
                    WriteAtomic(value);
                }
            }

            Validate(value);
            return value;
        }

        private void Validate(T value)
        {
            string? error = validate?.Invoke(value);
            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidDataException($"Plugin configuration '{path}' is invalid: {error}");
            }
        }

        private void WriteAtomic(T value)
        {
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
