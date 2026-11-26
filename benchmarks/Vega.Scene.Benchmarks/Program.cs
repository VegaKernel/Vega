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
        Results = new[] { lookup, section, observerChanges, personalOverlays, worldEditMoves, lobbyChurn }
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
