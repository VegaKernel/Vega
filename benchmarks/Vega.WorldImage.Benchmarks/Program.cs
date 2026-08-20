using System.Diagnostics;
using System.Text.Json;
using Vega.Terraria.Worlds;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: Vega.WorldImage.Benchmarks <small.wld> [medium.wld] [large.wld]");
    return 2;
}

var results = new List<WorldBenchmark>();
string temporaryRoot = Path.Combine(Path.GetTempPath(), "vega-world-image-benchmarks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryRoot);
try
{
    foreach (string suppliedPath in args)
    {
        string sourcePath = Path.GetFullPath(suppliedPath);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"World not found: {sourcePath}");
            return 2;
        }

        string worldDirectory = Path.Combine(temporaryRoot, results.Count.ToString());
        Directory.CreateDirectory(worldDirectory);
        string benchmarkSource = Path.Combine(worldDirectory, Path.GetFileName(sourcePath));
        string imagePath = Path.ChangeExtension(benchmarkSource, ".vworld");
        File.Copy(sourcePath, benchmarkSource);

        Measurement firstWldRead = Measure(() => Consume(File.ReadAllBytes(benchmarkSource)));
        Measurement warmWldRead = Measure(() => Consume(File.ReadAllBytes(benchmarkSource)));
        Measurement imageBuild = Measure(() => VegaWorldImageCompiler.Build(benchmarkSource, imagePath));
        WorldImageValidationResult validation = VegaWorldImageReader.Validate(benchmarkSource, imagePath);
        if (validation is not { IsValid: true, Manifest: not null })
        {
            throw new InvalidDataException($"Generated image failed validation: {validation.Reason}");
        }

        Measurement firstImageValidation = Measure(() =>
            Consume(VegaWorldImageReader.Validate(benchmarkSource, imagePath).Manifest!.TileRuns.Sha256));
        Measurement warmImageValidation = Measure(() =>
            Consume(VegaWorldImageReader.Validate(benchmarkSource, imagePath).Manifest!.TileRuns.Sha256));
        results.Add(new WorldBenchmark(
            Path.GetFileName(sourcePath),
            validation.Manifest.MaxTilesX,
            validation.Manifest.MaxTilesY,
            new FileInfo(benchmarkSource).Length,
            new FileInfo(imagePath).Length,
            firstWldRead,
            warmWldRead,
            imageBuild,
            firstImageValidation,
            warmImageValidation));
    }

    Console.WriteLine(JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
finally
{
    Directory.Delete(temporaryRoot, recursive: true);
}

static Measurement Measure(Action action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    int gen0 = GC.CollectionCount(0);
    int gen1 = GC.CollectionCount(1);
    int gen2 = GC.CollectionCount(2);
    long peakBefore = Process.GetCurrentProcess().PeakWorkingSet64;
    long started = Stopwatch.GetTimestamp();
    action();
    TimeSpan duration = Stopwatch.GetElapsedTime(started);
    using Process process = Process.GetCurrentProcess();
    return new Measurement(
        duration.TotalMilliseconds,
        GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
        Math.Max(0, process.PeakWorkingSet64 - peakBefore),
        GC.CollectionCount(0) - gen0,
        GC.CollectionCount(1) - gen1,
        GC.CollectionCount(2) - gen2);
}

static void Consume<T>(T value) => GC.KeepAlive(value);

internal sealed record WorldBenchmark(
    string World,
    int Width,
    int Height,
    long WldBytes,
    long VworldBytes,
    Measurement FirstWldRead,
    Measurement WarmWldRead,
    Measurement WorldImageBuild,
    Measurement FirstWorldImageValidation,
    Measurement WarmWorldImageValidation);

internal sealed record Measurement(
    double DurationMilliseconds,
    long AllocatedBytes,
    long PeakWorkingSetDeltaBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);
