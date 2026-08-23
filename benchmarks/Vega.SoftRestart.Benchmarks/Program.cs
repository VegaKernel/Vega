using System.Diagnostics;
using System.Text.Json;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Core.Modules;

const int cycles = 100;
int created = 0;
RestartableModuleGraph Factory(IReadOnlySet<string>? _) =>
    new(new EmptyHost(), ["benchmark.module"], () => created++);

using var coordinator = new SoftRestartCoordinator(
    new SoftRestartOptions(true, 5, 10, 60),
    Factory,
    new NullLogger());
await coordinator.RestartAsync(null); // JIT warmup.
GC.Collect();
GC.WaitForPendingFinalizers();
long retainedBefore = GC.GetTotalMemory(true);
long allocatedBefore = GC.GetTotalAllocatedBytes(true);
long startedAt = Stopwatch.GetTimestamp();
int rejected = 0;
for (int index = 0; index < cycles; index++)
{
    SoftRestartResult result = await coordinator.RestartAsync(null);
    if (result.Status != SoftRestartStatus.Succeeded) rejected++;
}

TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt);
long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
long retained = GC.GetTotalMemory(true) - retainedBefore;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Runtime = Environment.Version.ToString(),
    Cycles = cycles,
    TotalMilliseconds = elapsed.TotalMilliseconds,
    MillisecondsPerRestart = elapsed.TotalMilliseconds / cycles,
    AllocatedBytes = allocated,
    AllocatedBytesPerRestart = allocated / cycles,
    RetainedBytesAfterCollection = retained,
    RejectedRestarts = rejected,
    ActivatedGenerations = created
}, new JsonSerializerOptions { WriteIndented = true }));

sealed class EmptyHost : IVegaModuleHost
{
    public void Start() { }
    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => [];
    public void Dispose() { }
}

sealed class NullLogger : IOperationsLogger
{
    public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null,
        string? correlationId = null) { }
}
