using System.Diagnostics;
using System.Globalization;
using Vega.Contracts.Operations;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

/// <summary>Builds a new native template in a disposable child before catalog publication.</summary>
internal sealed class SandboxSchematicTemplateBuilder(string baseDirectory, string executablePath, SandboxOptions options,
    IOperationsLogger logger)
{
    private readonly string templatesRoot = Path.GetFullPath(Path.Combine(baseDirectory, "data", "sandboxes", "templates"));

    public async ValueTask<string> BuildAsync(WorldId worldId, string baseWorldPath, SandboxSchematicBootstrap schematic,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(baseWorldPath), input = Path.GetFullPath(schematic.SchematicPath);
        long maximumBytes = checked((long)options.MaximumInstanceMegabytes * 1024 * 1024);
        if (schematic.X < 0 || schematic.Y < 0 || !File.Exists(source) || !File.Exists(input) ||
            !string.Equals(Path.GetExtension(source), ".wld", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(input), ".trschem", StringComparison.OrdinalIgnoreCase) ||
            new FileInfo(source).Length > maximumBytes)
            throw new InvalidDataException("Schematic creation requires an existing bounded .wld base and .trschem file with non-negative tile coordinates.");
        Directory.CreateDirectory(templatesRoot);
        string directory = Path.Combine(templatesRoot, $"{worldId.Value}-{Guid.NewGuid():N}");
        string output = Path.Combine(directory, "world.wld");
        bool managedDll = string.Equals(Path.GetExtension(executablePath), ".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managedDll ? "dotnet" : executablePath)
        {
            WorkingDirectory = templatesRoot,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (managedDll) start.ArgumentList.Add(executablePath);
        start.ArgumentList.Add("--vega-build-schematic-template");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(schematic.X.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(schematic.Y.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(maximumBytes.ToString(CultureInfo.InvariantCulture));
        using var process = new Process { StartInfo = start };
        bool started = false, completed = false;
        try
        {
            process.OutputDataReceived += (_, args) => LogLine(worldId, args.Data, false);
            process.ErrorDataReceived += (_, args) => LogLine(worldId, args.Data, true);
            if (!process.Start()) throw new IOException("Native schematic template builder did not start.");
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.StartTimeoutSeconds + options.GracefulStopTimeoutSeconds));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new InvalidDataException("Native schematic template build timed out."); }
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(directory, "complete")) || !File.Exists(output) ||
                new FileInfo(output).Length is <= 0 || new FileInfo(output).Length > maximumBytes)
                throw new InvalidDataException("Native schematic template build or save failed; see schematic builder diagnostics.");
            completed = true;
            return output;
        }
        catch (System.ComponentModel.Win32Exception ex)
        { throw new IOException("Native schematic template builder could not be started.", ex); }
        finally
        {
            if (started && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(stopTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
                { LogLine(worldId, $"Could not terminate failed builder: {ex.Message}", true); }
            }
            if (!completed && (!started || process.HasExited)) Remove(output);
        }
    }

    public void Remove(string output)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (directory is null || !string.Equals(Path.GetDirectoryName(directory), templatesRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(output), "world.wld", StringComparison.Ordinal) || !Directory.Exists(directory)) return;
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LogLine(new WorldId("schematic-build"), $"Could not clean up uncommitted generated template: {ex.Message}", true); }
    }

    private void LogLine(WorldId worldId, string? line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try { logger.Log(error ? OperationsLogLevel.Warning : OperationsLogLevel.Information, "sandbox-schematic", $"{worldId}: {line}"); }
        catch { /* A diagnostics subscriber must not terminate the template builder. */ }
    }
}
