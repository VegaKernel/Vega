using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

public sealed record TerminalUiPanelLayout(
    bool Visible,
    int XPercent,
    int YPercent,
    int WidthPercent,
    int HeightPercent);

public sealed record TerminalUiLayoutOptions(
    int RefreshIntervalMilliseconds,
    int HistorySamples,
    IReadOnlyDictionary<string, TerminalUiPanelLayout> Windows)
{
    public const string Overview = "overview";
    public const string Performance = "performance";
    public const string Memory = "memory";
    public const string Network = "network";
    public const string Players = "players";
    public const string Modules = "modules";
    public const string Logs = "logs";
    public const string Chat = "chat";
    public const string Regions = "regions";
    public const string Moderation = "moderation";

    public static IReadOnlyList<string> WindowNames { get; } =
    [
        Overview,
        Performance,
        Memory,
        Network,
        Players,
        Modules,
        Logs,
        Chat,
        Regions,
        Moderation
    ];

    public static TerminalUiLayoutOptions Default { get; } = new(
        750,
        120,
        new Dictionary<string, TerminalUiPanelLayout>(StringComparer.OrdinalIgnoreCase)
        {
            // Logs are the primary work surface. The right side is a dense operational stack.
            [Logs] = new(true, 0, 0, 56, 100),
            [Overview] = new(true, 56, 0, 22, 24),
            [Players] = new(true, 78, 0, 22, 24),
            [Performance] = new(true, 56, 24, 44, 22),
            [Memory] = new(true, 56, 46, 44, 18),
            [Network] = new(true, 56, 64, 22, 18),
            [Modules] = new(true, 78, 64, 22, 18),
            [Chat] = new(true, 56, 82, 44, 18),
            [Regions] = new(false, 56, 0, 44, 46),
            [Moderation] = new(false, 56, 46, 44, 36)
        });

    public TerminalUiPanelLayout GetWindow(string name) => Windows[name];

    public static TerminalUiLayoutOptions Normalize(TerminalUiLayoutOptions? candidate)
    {
        TerminalUiLayoutOptions defaults = Default;
        int refresh = Math.Clamp(candidate?.RefreshIntervalMilliseconds ?? defaults.RefreshIntervalMilliseconds, 250, 5000);
        int history = Math.Clamp(candidate?.HistorySamples ?? defaults.HistorySamples, 20, 600);
        var windows = new Dictionary<string, TerminalUiPanelLayout>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in WindowNames)
        {
            TerminalUiPanelLayout fallback = defaults.Windows[name];
            TerminalUiPanelLayout source = Find(candidate?.Windows, name) ?? fallback;
            windows[name] = new TerminalUiPanelLayout(
                source.Visible,
                Math.Clamp(source.XPercent, 0, 95),
                Math.Clamp(source.YPercent, 0, 95),
                Math.Clamp(source.WidthPercent, 15, 100),
                Math.Clamp(source.HeightPercent, 10, 100));
        }

        return new TerminalUiLayoutOptions(refresh, history, windows);
    }

    private static TerminalUiPanelLayout? Find(
        IReadOnlyDictionary<string, TerminalUiPanelLayout>? windows,
        string name)
    {
        if (windows is null)
        {
            return null;
        }

        if (windows.TryGetValue(name, out TerminalUiPanelLayout? panel))
        {
            return panel;
        }

        return windows.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

public sealed class TerminalUiLayoutStore(string path, IOperationsLogger logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public TerminalUiLayoutOptions Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return TerminalUiLayoutOptions.Default;
            }

            string json = File.ReadAllText(Path);
            return TerminalUiLayoutOptions.Normalize(JsonSerializer.Deserialize<TerminalUiLayoutOptions>(json, SerializerOptions));
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "terminal-ui",
                $"Terminal UI layout '{Path}' is invalid; using the default layout.",
                ex);
            return TerminalUiLayoutOptions.Default;
        }
    }

    public bool TrySave(TerminalUiLayoutOptions layout)
    {
        string? directory = System.IO.Path.GetDirectoryName(Path);
        string temporaryPath = Path + ".tmp";
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(TerminalUiLayoutOptions.Normalize(layout), SerializerOptions);
            File.WriteAllText(temporaryPath, json + Environment.NewLine);
            File.Move(temporaryPath, Path, true);
            return true;
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "terminal-ui",
                $"Terminal UI layout '{Path}' could not be saved.",
                ex);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception)
            {
                // A stale temporary layout is harmless and must not interrupt server shutdown.
            }
        }
    }
}

public sealed record OperationsMetricSample(
    double TicksPerSecond,
    double CpuPercent,
    double WorkingSetMiB,
    double GcHeapMiB,
    double InboundKiBPerSecond,
    double OutboundKiBPerSecond);

public sealed class OperationsMetricHistory
{
    private readonly int capacity;
    private readonly Queue<OperationsMetricSample> samples;
    private OperationsSnapshot? previous;

    public OperationsMetricHistory(int capacity)
    {
        this.capacity = Math.Clamp(capacity, 20, 600);
        samples = new Queue<OperationsMetricSample>(this.capacity);
    }

    public IReadOnlyList<OperationsMetricSample> Samples => samples.ToArray();

    public OperationsMetricSample Add(OperationsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        double elapsedSeconds = previous is null
            ? 0
            : (snapshot.Runtime.SampledAt - previous.Runtime.SampledAt).TotalSeconds;
        double cpuPercent = elapsedSeconds <= 0 || previous is null
            ? 0
            : Math.Clamp(
                (snapshot.Runtime.Host.TotalProcessorTime - previous.Runtime.Host.TotalProcessorTime).TotalSeconds
                / elapsedSeconds
                / Math.Max(1, snapshot.Runtime.Host.ProcessorCount)
                * 100,
                0,
                100);
        double inboundRate = Rate(
            snapshot.Network.InboundBytes,
            previous?.Network.InboundBytes,
            elapsedSeconds) / 1024d;
        double outboundRate = Rate(
            snapshot.Network.OutboundBytes,
            previous?.Network.OutboundBytes,
            elapsedSeconds) / 1024d;
        var sample = new OperationsMetricSample(
            snapshot.Server.TicksPerSecond,
            cpuPercent,
            snapshot.Runtime.Host.WorkingSetBytes / 1024d / 1024d,
            snapshot.Runtime.GcHeapBytes / 1024d / 1024d,
            inboundRate,
            outboundRate);

        if (samples.Count == capacity)
        {
            samples.Dequeue();
        }

        samples.Enqueue(sample);
        previous = snapshot;
        return sample;
    }

    private static double Rate(long current, long? previous, double elapsedSeconds) =>
        previous is null || elapsedSeconds <= 0 || current < previous
            ? 0
            : (current - previous.Value) / elapsedSeconds;
}
