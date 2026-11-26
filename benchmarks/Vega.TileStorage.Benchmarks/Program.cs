using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vega.Terraria.Worlds;
using ITile = global::Terraria.ITile;
using Tile = global::Terraria.Tile;

if (args.Length != 2 || args[0] is not ("default" or "paged") || args[1] is not ("small" or "medium"))
    throw new ArgumentException("Usage: Vega.TileStorage.Benchmarks default|paged small|medium");

bool paged = args[0] == "paged";
using var scope = paged ? new NativeTileStorageScope() : null;
global::Terraria.Program.SavePath = Path.GetTempPath();
global::TerrariaRuntime.ICollection<ITile> grid = scope?.Complete() ?? GetNativeGrid();
if (paged != (grid is PagedNativeTileCollection)) throw new InvalidOperationException("Canonical tile factory was not selected.");

// Synthetic references isolate grid overhead from the unchanged native Tile objects.
// No world is loaded, created or saved by this process.
int width = args[1] == "small" ? 4200 : 6400;
int height = args[1] == "small" ? 1200 : 1800;
ITile shared = new Tile { type = 1 };
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
long mapBefore = GC.GetAllocatedBytesForCurrentThread();
long started = Stopwatch.GetTimestamp();
for (int x = 0; x < width; x++)
    for (int y = 0; y < height; y++) grid[x, y] = shared;
double fillMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
long mapAllocated = GC.GetAllocatedBytesForCurrentThread() - mapBefore;

started = Stopwatch.GetTimestamp();
long checksum = 0;
for (int x = 0; x < width; x++)
    for (int y = 0; y < height; y++) checksum += grid[x, y].type;
double scanMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
if (checksum != (long)width * height || grid[width, height] is not null || !ReferenceEquals(grid[0, 0], shared))
    throw new InvalidOperationException("Synthetic native grid did not preserve references or null margin.");

// Measure current native Tile objects and the minimal coordinate proxy shape separately.
const int sampleCount = 250_000;
var tiles = new ITile[sampleCount];
long tileBefore = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < tiles.Length; i++) tiles[i] = new Tile();
long tileBytes = GC.GetAllocatedBytesForCurrentThread() - tileBefore;
var proxies = new CoordinateProxyFootprint[sampleCount];
long proxyBefore = GC.GetAllocatedBytesForCurrentThread();
for (int i = 0; i < proxies.Length; i++) proxies[i] = new CoordinateProxyFootprint(grid, i, 0);
long proxyBytes = GC.GetAllocatedBytesForCurrentThread() - proxyBefore;
GC.KeepAlive(tiles);
GC.KeepAlive(proxies);

Console.WriteLine(JsonSerializer.Serialize(new
{
    Variant = args[0],
    ActiveWidth = width,
    ActiveHeight = height,
    FactoryWidth = grid.Width,
    FactoryHeight = grid.Height,
    PointerBytes = IntPtr.Size,
    PageCount = (grid as PagedNativeTileCollection)?.AllocatedPageCount,
    ReferenceMapFillAllocatedBytes = mapAllocated,
    FillMilliseconds = fillMs,
    ScanMilliseconds = scanMs,
    Checksum = checksum,
    NativeTileAllocatedBytesPerObject = tileBytes / (double)sampleCount,
    PackedTilePayloadBytes = Unsafe.SizeOf<PackedTilePayload>(),
    CoordinateProxyFootprintBytesPerObject = proxyBytes / (double)sampleCount
}, new JsonSerializerOptions { WriteIndented = true }));

[MethodImpl(MethodImplOptions.NoInlining)]
static global::TerrariaRuntime.ICollection<ITile> GetNativeGrid() => global::Terraria.Main.tile;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct PackedTilePayload
{
    public ushort Type, Wall, Header;
    public short FrameX, FrameY;
    public byte Liquid, Header1, Header2, Header3;
}

// Models only the reference + two coordinate fields of a FakeProvider-style wrapper;
// it is deliberately not a replacement ITile implementation.
sealed record CoordinateProxyFootprint(object Storage, int X, int Y);
