using System.Drawing;

namespace Vega.TerminalUI;

internal static class TerminalUiTiling
{
    private const int EdgeTolerance = 1;

    public static string? FindSwapTarget(
        string movingName,
        Rectangle moving,
        IReadOnlyDictionary<string, Rectangle> frames)
    {
        string? target = null;
        int largestIntersection = 0;
        foreach ((string name, Rectangle frame) in frames)
        {
            if (string.Equals(name, movingName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Rectangle intersection = Rectangle.Intersect(moving, frame);
            int area = intersection.Width * intersection.Height;
            if (area > largestIntersection)
            {
                target = name;
                largestIntersection = area;
            }
        }

        return target;
    }

    public static bool TryResizeSharedEdges(
        string movingName,
        Rectangle before,
        Rectangle after,
        IReadOnlyDictionary<string, Rectangle> frames,
        Size desktop,
        out IReadOnlyDictionary<string, Rectangle> resized,
        int minimumWidth = 12,
        int minimumHeight = 5)
    {
        resized = frames;
        if (desktop.Width <= 0 || desktop.Height <= 0 ||
            after.Width < minimumWidth || after.Height < minimumHeight ||
            after.Left < 0 || after.Top < 0 || after.Right > desktop.Width || after.Bottom > desktop.Height)
        {
            return false;
        }

        var result = new Dictionary<string, Rectangle>(frames, StringComparer.OrdinalIgnoreCase)
        {
            [movingName] = after
        };
        bool adjustedNeighbour = false;

        foreach ((string name, Rectangle neighbour) in frames)
        {
            if (string.Equals(name, movingName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Rectangle changed = neighbour;
            if (before.Left != after.Left &&
                Near(neighbour.Right, before.Left) &&
                Overlaps(neighbour.Top, neighbour.Bottom, before.Top, before.Bottom))
            {
                changed.Width = after.Left - neighbour.Left;
            }

            if (before.Right != after.Right &&
                Near(neighbour.Left, before.Right) &&
                Overlaps(neighbour.Top, neighbour.Bottom, before.Top, before.Bottom))
            {
                changed.X = after.Right;
                changed.Width = neighbour.Right - after.Right;
            }

            if (before.Top != after.Top &&
                Near(neighbour.Bottom, before.Top) &&
                Overlaps(neighbour.Left, neighbour.Right, before.Left, before.Right))
            {
                changed.Height = after.Top - neighbour.Top;
            }

            if (before.Bottom != after.Bottom &&
                Near(neighbour.Top, before.Bottom) &&
                Overlaps(neighbour.Left, neighbour.Right, before.Left, before.Right))
            {
                changed.Y = after.Bottom;
                changed.Height = neighbour.Bottom - after.Bottom;
            }

            if (changed == neighbour)
            {
                continue;
            }

            if (changed.Width < minimumWidth || changed.Height < minimumHeight)
            {
                return false;
            }

            result[name] = changed;
            adjustedNeighbour = true;
        }

        if (!adjustedNeighbour)
        {
            return false;
        }

        resized = result;
        return true;
    }

    private static bool Near(int first, int second) => Math.Abs(first - second) <= EdgeTolerance;

    private static bool Overlaps(int firstStart, int firstEnd, int secondStart, int secondEnd) =>
        Math.Min(firstEnd, secondEnd) > Math.Max(firstStart, secondStart);
}
