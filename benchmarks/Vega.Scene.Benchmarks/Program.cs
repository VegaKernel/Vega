using System.Diagnostics;
using System.Text.Json;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Core.Scenes;
using Vega.Modules.Scenes;

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
int lookupIndex = 0;
void Lookup()
{
    int value = lookupIndex++;
    int x = value & 255;
    int y = value >> 8 & 255;
    if (!runtime.TryResolveTile(WorldId.Primary, observer, x, y, out SceneTile resolved, out _) || resolved != tile)
        throw new InvalidOperationException("Overlay lookup mismatch.");
}
Measure("warmup", 10_000, Lookup);
lookupIndex = 0;
Measurement lookup = Measure("overlay_lookup_64", lookupCount, Lookup);
int sectionIndex = 0;
Measurement section = Measure("compose_64x64", 64 * 64, () =>
{
    int value = sectionIndex++;
    int x = value & 63;
    int y = value >> 6 & 63;
    if (!runtime.TryResolveTile(WorldId.Primary, observer, x, y, out SceneTile resolved, out _) || resolved != tile)
        throw new InvalidOperationException("Section lookup mismatch.");
});

// Native tile-square interception normally probes a small region; many probes
// miss every overlay. Include both outcomes and cross-bucket rectangles.
int regionIndex = 0;
void QueryRegion()
{
    int value = regionIndex++;
    int x = (value * 17 & 1023) - 32;
    int y = (value * 31 >> 4 & 1023) - 32;
    var bounds = new SceneBounds(x, y, 5, 5);
    bool expected = x < 256 && x + 5 > 0 && y < 256 && y + 5 > 0;
    if (runtime.HasOverlayInRegion(WorldId.Primary, bounds, observer) != expected)
        throw new InvalidOperationException("Overlay region query mismatch.");
}
Measure("region_warmup", 50_000, QueryRegion);
regionIndex = 0;
Measurement regionQueries = Measure("region_intersection_64_mixed", 1_000_000, QueryRegion);

var nativeSection = new SceneBounds(32, 32, 200, 150);
int expectedSection = runtime.SnapshotScenes().SelectMany(scene => scene.Overlays)
    .Count(overlay => overlay.Enabled && overlay.Bounds.Intersects(nativeSection));
void SectionSnapshot()
{
    int count = runtime.SnapshotScenes().Where(scene => scene.WorldId == WorldId.Primary)
        .SelectMany(static scene => scene.Overlays).Count(overlay => overlay.Enabled && overlay.Bounds.Intersects(nativeSection));
    if (count != expectedSection) throw new InvalidOperationException("Section snapshot mismatch.");
}
void SectionProjection()
{
    if (runtime.EnumerateOverlaysInRegion(WorldId.Primary, nativeSection).Count() != expectedSection)
        throw new InvalidOperationException("Section projection mismatch.");
}
Measure("section_snapshot_warmup", 1000, SectionSnapshot);
Measure("section_projection_warmup", 1000, SectionProjection);
Measurement sectionSnapshot = Measure("section_snapshot_64", 10_000, SectionSnapshot);
Measurement sectionProjection = Measure("section_projection_64", 10_000, SectionProjection);

var sparseRuntime = new SceneRuntime();
using var sparseScene = sparseRuntime.CreateScene(WorldId.Primary, "sparse", "benchmark");
for (int i = 0; i < 1024; i++)
    sparseScene.AddOverlay(new SceneOverlayDefinition(default, $"sparse-{i}", new SceneBounds(i % 32 * 64, i / 32 * 64, 32, 32),
        i, SceneObserverScope.Global, area));
SceneOverlayDefinition[] flat = sparseRuntime.SnapshotScenes().Single().Overlays.Reverse().ToArray();
var queries = Enumerable.Range(0, 4096).Select(i => new SceneBounds((i * 173 & 8191) - 64, (i * 3571 & 8191) - 64, 5, 5)).ToArray();
bool[] expectedQueries = queries.Select(bounds => flat.Any(overlay => overlay.Bounds.Intersects(bounds))).ToArray();
int sparseIndex = 0;
void SparseFlat()
{
    int index = sparseIndex++ & (queries.Length - 1);
    bool found = false;
    foreach (var overlay in flat)
        if (overlay.Bounds.Intersects(queries[index]) && overlay.Observers.Contains(observer)) { found = true; break; }
    if (found != expectedQueries[index]) throw new InvalidOperationException("Flat query mismatch.");
}
void SparseIndexed()
{
    int index = sparseIndex++ & (queries.Length - 1);
    if (sparseRuntime.HasOverlayInRegion(WorldId.Primary, queries[index], observer) != expectedQueries[index])
        throw new InvalidOperationException("Sparse indexed query mismatch.");
}
Measure("sparse_flat_warmup", 10_000, SparseFlat);
Measure("sparse_indexed_warmup", 10_000, SparseIndexed);
sparseIndex = 0;
Measurement sparseFlat = Measure("region_flat_1024_mixed", 200_000, SparseFlat);
sparseIndex = 0;
Measurement sparseIndexed = Measure("region_indexed_1024_mixed", 200_000, SparseIndexed);

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

var consumerRuntime = new SceneRuntime();
var consumerVisibility = new SceneVisibilityRuntime();
using var worldEdit = new WorldEditPreviewService(consumerRuntime);
using var lobbyVisuals = new LobbyVisualService(consumerRuntime);
using var clustering = new FakeProviderClusteringService(consumerVisibility);
using var lobby = new LobbySceneCompositionService(lobbyVisuals, clustering);
var previewObserver = new PlayerId(17);
var previewMask = new SceneTileArea(32, 32, Enumerable.Repeat(new SceneTile(2, Flags: SceneTileFlags.Active), 32 * 32).ToArray());
worldEdit.Show(previewObserver, "large-copy", new SceneBounds(0, 0, 32, 32), area, previewMask);
int previewMove = 0;
Measurement worldEditMoves = Measure("worldedit_preview_move_with_mask", 10_000, () =>
{
    int x = previewMove++ & 127;
    worldEdit.Move(previewObserver, new SceneBounds(x, 64, 32, 32), area, previewMask);
});

PlayerId[] lobbyObservers = Enumerable.Range(0, 200).Select(static slot => new PlayerId(slot)).ToArray();
var entities = Enumerable.Range(0, 300)
    .Select(static slot => new SceneEntity(SceneEntityKind.Npc, slot))
    .ToArray();
lobby.ShowFrame("spawn", new SceneBounds(96, 96, 32, 32), area, lobbyObservers);
lobby.HidePortal("spawn", new SceneBounds(128, 96, 8, 12), lobbyObservers);
lobby.SetCluster("spawn", lobbyObservers, entities);
int churn = 0;
Measurement lobbyChurn = Measure("lobby_composition_observer_churn_200x300", 2_000, () =>
{
    var removed = new PlayerId(churn++ % 200);
    lobby.RemoveObserver(removed);
    clustering.SetObservers("spawn:cluster", lobbyObservers);
});

Console.WriteLine(JsonSerializer.Serialize(
    new
    {
        Runtime = Environment.Version.ToString(),
        ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        OverlayCount = overlayCount,
        Results = new[] { lookup, section, regionQueries, sectionSnapshot, sectionProjection, sparseFlat, sparseIndexed,
            observerChanges, personalOverlays, worldEditMoves, lobbyChurn }
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
