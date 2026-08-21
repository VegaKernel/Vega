using System.Drawing;

namespace Vega.TerminalUI;

internal static class TerminalUiSnapping
{
    public static Point SnapPosition(
        Rectangle moving,
        Size desktop,
        IEnumerable<Rectangle> neighbours,
        int threshold = 2)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0 || threshold < 0)
        {
            return moving.Location;
        }

        Rectangle[] others = neighbours.ToArray();
        int x = Math.Clamp(
            moving.X + FindDelta(
                moving.Left,
                moving.Right,
                [0, desktop.Width, .. others.SelectMany(static frame => new[] { frame.Left, frame.Right })],
                threshold),
            0,
            Math.Max(0, desktop.Width - moving.Width));
        int y = Math.Clamp(
            moving.Y + FindDelta(
                moving.Top,
                moving.Bottom,
                [0, desktop.Height, .. others.SelectMany(static frame => new[] { frame.Top, frame.Bottom })],
                threshold),
            0,
            Math.Max(0, desktop.Height - moving.Height));
        return new Point(x, y);
    }

    private static int FindDelta(int start, int end, IEnumerable<int> anchors, int threshold)
    {
        int best = 0;
        int bestDistance = threshold + 1;
        foreach (int anchor in anchors)
        {
            Consider(anchor - start);
            Consider(anchor - end);
        }

        return best;

        void Consider(int delta)
        {
            int distance = Math.Abs(delta);
            if (distance <= threshold && distance < bestDistance)
            {
                best = delta;
                bestDistance = distance;
            }
        }
    }
}
