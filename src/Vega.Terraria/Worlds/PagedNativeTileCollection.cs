namespace Vega.Terraria.Worlds;

/// <summary>
/// Sparse reference pages for the native maximum-sized tile grid. Tiles themselves remain
/// Terraria's objects: assignment, aliases, nulls and detached object lifetime are unchanged.
/// </summary>
public sealed class PagedNativeTileCollection : global::TerrariaRuntime.ICollection<global::Terraria.ITile>
{
    private const int PageShift = 6;
    private const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;
    private readonly int pageRows;
    private readonly global::Terraria.ITile?[]?[] pages;
    private int allocatedPages;

    public PagedNativeTileCollection(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        pageRows = PageCount(height);
        pages = new global::Terraria.ITile?[checked(PageCount(width) * pageRows)][];
    }

    public int Width { get; }
    public int Height { get; }
    public int AllocatedPageCount => Volatile.Read(ref allocatedPages);

    public global::Terraria.ITile this[int x, int y]
    {
        get
        {
            int pageIndex = GetPageIndex(x, y);
            var page = Volatile.Read(ref pages[pageIndex]);
            return page is null ? null! : Volatile.Read(ref page[GetCellIndex(x, y)])!;
        }
        set
        {
            int pageIndex = GetPageIndex(x, y);
            var page = Volatile.Read(ref pages[pageIndex]);
            if (page is null)
            {
                if (value is null) return; // Native clear of an unallocated null margin is a no-op.
                var created = new global::Terraria.ITile?[PageSize * PageSize];
                page = Interlocked.CompareExchange(ref pages[pageIndex], created, null);
                if (page is null)
                {
                    Interlocked.Increment(ref allocatedPages);
                    page = created;
                }
            }
            Volatile.Write(ref page[GetCellIndex(x, y)], value);
        }
    }

    private int GetPageIndex(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) throw new IndexOutOfRangeException();
        return (x >> PageShift) * pageRows + (y >> PageShift);
    }

    private static int GetCellIndex(int x, int y) => ((x & PageMask) << PageShift) + (y & PageMask);
    private static int PageCount(int length) => length == 0 ? 0 : ((length - 1) >> PageShift) + 1;
}
