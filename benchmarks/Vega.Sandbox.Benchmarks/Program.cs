using System.Diagnostics;
using System.Text.Json;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Sandboxes;

if (args.FirstOrDefault() == "--ssc-checkpoints")
{
    await SscCheckpointBenchmark.RunAsync(args);
    return;
}

if (args.Any(static argument => argument.StartsWith("--vega-backend-", StringComparison.OrdinalIgnoreCase)))
{
    SandboxBackendLaunchOptions launch = SandboxBackendLaunchOptions.Parse(args);
    using var reporter = new SandboxBackendReporter(launch, new ReadyLifecycle(), new ReadyServer(), new EmptyPlayers());
    while (await Console.In.ReadLineAsync() is { } line &&
           !line.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase))
    {
    }

    return;
}

int templateMegabytes = args.Length == 0 ? 32 : int.Parse(args[0]);
int sandboxCount = args.Length < 2 ? 4 : int.Parse(args[1]);
string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-benchmarks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string template = Path.Combine(root, "template.wld");
    await using (FileStream stream = new(template, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        stream.SetLength(checked((long)templateMegabytes * 1024 * 1024));
    }

    var options = SandboxOptions.Default with
    {
        MaximumActiveWorlds = Math.Max(sandboxCount, 1),
        StartTimeoutSeconds = 15,
        GracefulStopTimeoutSeconds = 5
    };
    using var control = new SandboxControlService(
        root,
        options,
        new NullLogger(),
        System.Reflection.Assembly.GetExecutingAssembly().Location);
    var created = 0;
    Measurement materialize = await MeasureAsync("materialize", sandboxCount, async () =>
    {
        string id = $"bench-{created++}";
        SandboxOperationResult result = await control.CreateAsync(new SandboxCreateRequest(
            id,
            id,
            template,
            SandboxPersistenceMode.Temporary,
            16,
            new SandboxAccessPolicy(SandboxAccessMode.Public),
            TimeSpan.FromMinutes(30),
            SandboxSscMode.Disabled,
            new HashSet<string>()));
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    });
    var startedWorld = 0;
    Measurement coldStart = await MeasureAsync("signed_process_readiness", sandboxCount, async () =>
    {
        SandboxOperationResult result = await control.StartAsync(new WorldId($"bench-{startedWorld++}"));
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    });
    Measurement snapshots = await MeasureAsync("operations_snapshot", 100_000, () =>
    {
        GC.KeepAlive(control.Snapshot());
        return ValueTask.CompletedTask;
    });
    var stoppedWorld = sandboxCount;
    Measurement shutdown = await MeasureAsync("graceful_shutdown", sandboxCount, async () =>
    {
        SandboxOperationResult result = await control.StopAsync(new WorldId($"bench-{--stoppedWorld}"));
        if (!result.IsSuccess) throw new InvalidOperationException(result.Message);
    });

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Runtime = Environment.Version.ToString(),
        TemplateMegabytes = templateMegabytes,
        SandboxCount = sandboxCount,
        Results = new[] { materialize, coldStart, snapshots, shutdown }
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    Directory.Delete(root, true);
}

static async ValueTask<Measurement> MeasureAsync(string name, int operations, Func<ValueTask> action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long allocated = GC.GetTotalAllocatedBytes(true);
    long started = Stopwatch.GetTimestamp();
    for (var index = 0; index < operations; index++) await action();
    TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
    return new Measurement(name, operations, elapsed.TotalMilliseconds,
        elapsed.TotalMilliseconds * 1_000_000 / operations,
        GC.GetTotalAllocatedBytes(true) - allocated);
}

internal sealed record Measurement(
    string Name,
    int Operations,
    double TotalMilliseconds,
    double NanosecondsPerOperation,
    long AllocatedBytes);

internal sealed class ReadyLifecycle : IRuntimeLifecycle
{
    public RuntimeLifecycleStage CurrentStage => RuntimeLifecycleStage.FullyWarmed;
    public TimeSpan Elapsed => TimeSpan.FromSeconds(1);
    public event Action<RuntimeLifecycleTransition>? Transitioned { add { } remove { } }
}

internal sealed class ReadyServer : IServerRuntimeStateReader
{
    public ServerRuntimeStateSnapshot Read() => new("benchmark", 16, 7780, true, 60, DateTimeOffset.UtcNow);
}

internal sealed class EmptyPlayers : IPlayerDirectory
{
    public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Array.Empty<PlayerSnapshot>();
    public bool TryGet(PlayerId playerId, out PlayerSnapshot? player) { player = null; return false; }
    public bool TryFindExact(string name, out PlayerSnapshot? player) { player = null; return false; }
}

internal sealed class NullLogger : IOperationsLogger
{
    public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
}
