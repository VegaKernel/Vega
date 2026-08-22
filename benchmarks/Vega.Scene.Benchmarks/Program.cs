using System.Diagnostics;
using System.Text.Json;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;

const int overlayCount = 64;
const int lookupCount = 250_000;
var runtime = new SceneRuntime();
var leases = new List<ISceneLease>();
var tile = new SceneTile(1, Flags: SceneTileFlags.Active);
var area = new SceneTileArea(32, 32, Enumerable.Repeat(tile, 32 * 32).ToArray());
for (var index = 0; index < overlayCount; index++)
{
    ISceneLease lease = runtime.CreateScene(WorldId.Primary, $"scene-{index}", "benchmark");
    lease.AddOverlay(new SceneOverlayDefinition(
        default,
        $"overlay-{index}",
        new SceneBounds((index % 8) * 32, (index / 8) * 32, 32, 32),
        index,
        SceneObserverScope.Global,
        area));
    leases.Add(lease);
}

var observer = new PlayerId(1);
Measure("warmup", 1, () => runtime.TryResolveTile(WorldId.Primary, observer, 16, 16, out _, out _));
Measurement lookup = Measure("overlay_lookup_64", lookupCount, () =>
{
    int value = Environment.TickCount;
    int x = value & 255;
    int y = value >> 8 & 255;
    runtime.TryResolveTile(WorldId.Primary, observer, x, y, out _, out _);
});
Measurement section = Measure("compose_64x64", 64 * 64, () =>
{
    int value = Environment.TickCount;
    int x = value & 63;
    int y = value >> 6 & 63;
    runtime.TryResolveTile(WorldId.Primary, observer, x, y, out _, out _);
});

var visibility = new SceneVisibilityRuntime();
using IVisibilityLayerLease layer = visibility.CreateLayer(WorldId.Primary, "cluster", "benchmark", []);
Measurement observerChanges = Measure("observer_change_200", 1_000, () =>
    layer.SetObservers(Enumerable.Range(0, 200).Select(static slot => new PlayerId(slot))));

var personalRuntime = new SceneRuntime();
var personalLeases = new List<ISceneLease>();
Measurement personalOverlays = Measure("personal_overlay_create_16x16", 1_000, () =>
{
    int slot = personalLeases.Count % 200;
    ISceneLease lease = personalRuntime.CreateScene(WorldId.Primary, $"personal-{personalLeases.Count}", "benchmark");
    lease.AddOverlay(new SceneOverlayDefinition(
        default,
        "personal",
        new SceneBounds(0, 0, 32, 32),
        1,
        SceneObserverScope.Personal([new PlayerId(slot)]),
        area));
    personalLeases.Add(lease);
});

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        Runtime = Environment.Version.ToString(),
        ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        OverlayCount = overlayCount,
        Results = new[] { lookup, section, observerChanges, personalOverlays }
    },
    new JsonSerializerOptions { WriteIndented = true }));

foreach (ISceneLease lease in personalLeases) lease.Dispose();
foreach (ISceneLease lease in leases) lease.Dispose();

static Measurement Measure(string name, int operations, Action action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    for (var index = 0; index < operations; index++) action();
    TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
    return new Measurement(
        name,
        operations,
        elapsed.TotalMilliseconds,
        elapsed.TotalMilliseconds * 1_000_000 / operations,
        GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
}

internal sealed record Measurement(
    string Name,
    int Operations,
    double TotalMilliseconds,
    double NanosecondsPerOperation,
    long AllocatedBytes);
