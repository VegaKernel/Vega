using System.Drawing;
using NUnit.Framework;
using Vega.TerminalUI;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class TerminalUiSnappingTests
{
    [Test]
    public void SnapsToDesktopAndNeighbourEdgesWithoutChangingSize()
    {
        var moving = new Rectangle(3, 19, 20, 10);
        var neighbour = new Rectangle(25, 30, 30, 15);

        Point snapped = TerminalUiSnapping.SnapPosition(
            moving,
            new Size(100, 60),
            [neighbour],
            threshold: 3);

        Assert.That(snapped, Is.EqualTo(new Point(5, 20)));
    }

    [Test]
    public void LeavesWindowAloneOutsideThreshold()
    {
        var moving = new Rectangle(10, 10, 20, 10);

        Point snapped = TerminalUiSnapping.SnapPosition(
            moving,
            new Size(100, 60),
            [new Rectangle(50, 40, 20, 10)],
            threshold: 2);

        Assert.That(snapped, Is.EqualTo(moving.Location));
    }
}
