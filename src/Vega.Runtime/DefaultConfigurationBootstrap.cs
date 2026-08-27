using System.Reflection;
using System.Runtime.CompilerServices;

namespace Vega.Runtime;

/// <summary>
/// Restores missing operator configuration files from the canonical defaults embedded in Vega.Server.
/// Existing files are never replaced or rewritten.
/// </summary>
internal static class DefaultConfigurationBootstrap
{
    private const string ResourcePrefix = "Vega.DefaultConfig.";

    [ModuleInitializer]
    internal static void InitializeServerDefaults()
    {
        if (!string.Equals(
                Assembly.GetEntryAssembly()?.GetName().Name,
                "Vega.Server",
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            EnsureMissing(Path.Combine(AppContext.BaseDirectory, "config"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Vega could not restore missing configuration files: {ex.Message}");
        }
    }

    internal static IReadOnlyList<string> EnsureMissing(
        string configDirectory,
        Assembly? sourceAssembly = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        sourceAssembly ??= typeof(DefaultConfigurationBootstrap).Assembly;

        Directory.CreateDirectory(configDirectory);
        var created = new List<string>();

        foreach (string resourceName in sourceAssembly
                     .GetManifestResourceNames()
                     .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(static name => name, StringComparer.Ordinal))
        {
            string fileName = resourceName[ResourcePrefix.Length..];
            if (fileName.Length == 0 ||
                !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Invalid embedded Vega configuration resource '{resourceName}'.");
            }

            string destination = Path.Combine(configDirectory, fileName);
            if (File.Exists(destination))
            {
                continue;
            }

            using Stream source = sourceAssembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Embedded Vega configuration resource '{resourceName}' is unavailable.");

            string temporary = Path.Combine(
                configDirectory,
                $".{fileName}.{Guid.NewGuid():N}.tmp");

            try
            {
                using (var output = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           bufferSize: 16 * 1024,
                           FileOptions.SequentialScan))
                {
                    source.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(temporary, destination, overwrite: false);
                    created.Add(fileName);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another startup path won the create-only race. Never replace its file.
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return Array.AsReadOnly(created.ToArray());
    }
}
