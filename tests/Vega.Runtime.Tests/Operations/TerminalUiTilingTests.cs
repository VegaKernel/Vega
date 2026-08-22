using System.Drawing;
using NUnit.Framework;
using Vega.TerminalUI;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class TerminalUiTilingTests
{
    [Test]
    public void MovingSharedVerticalEdgeGrowsOneWindowAndShrinksItsNeighbour()
    {
        var left = new Rectangle(0, 0, 50, 30);
        var right = new Rectangle(50, 0, 50, 30);
        var frames = new Dictionary<string, Rectangle>
        {
            ["left"] = left,
            ["right"] = right
        };

        bool accepted = TerminalUiTiling.TryResizeSharedEdges(
            "left",
            left,
            new Rectangle(0, 0, 60, 30),
            frames,
            new Size(100, 30),
            out IReadOnlyDictionary<string, Rectangle> resized);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(resized["left"], Is.EqualTo(new Rectangle(0, 0, 60, 30)));
            Assert.That(resized["right"], Is.EqualTo(new Rectangle(60, 0, 40, 30)));
        });
    }

    [Test]
    public void MovingSharedHorizontalEdgeUpdatesEveryAdjacentTile()
    {
        var top = new Rectangle(0, 0, 100, 20);
        var frames = new Dictionary<string, Rectangle>
        {
            ["top"] = top,
            ["bottom-left"] = new Rectangle(0, 20, 50, 20),
            ["bottom-right"] = new Rectangle(50, 20, 50, 20)
        };

        bool accepted = TerminalUiTiling.TryResizeSharedEdges(
            "top",
            top,
            new Rectangle(0, 0, 100, 25),
            frames,
            new Size(100, 40),
            out IReadOnlyDictionary<string, Rectangle> resized);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(resized["bottom-left"], Is.EqualTo(new Rectangle(0, 25, 50, 15)));
            Assert.That(resized["bottom-right"], Is.EqualTo(new Rectangle(50, 25, 50, 15)));
        });
    }

    [Test]
    public void RejectsResizeThatWouldMakeNeighbourTooSmall()
    {
        var left = new Rectangle(0, 0, 50, 30);
        var frames = new Dictionary<string, Rectangle>
        {
            ["left"] = left,
            ["right"] = new Rectangle(50, 0, 50, 30)
        };

        bool accepted = TerminalUiTiling.TryResizeSharedEdges(
            "left",
            left,
            new Rectangle(0, 0, 95, 30),
            frames,
            new Size(100, 30),
            out _);

        Assert.That(accepted, Is.False);
    }

    [Test]
    public void TitleDragChoosesTileWithLargestOverlap()
    {
        var frames = new Dictionary<string, Rectangle>
        {
            ["moving"] = new Rectangle(0, 0, 40, 20),
            ["small-overlap"] = new Rectangle(40, 0, 20, 20),
            ["target"] = new Rectangle(60, 0, 40, 20)
        };

        string? target = TerminalUiTiling.FindSwapTarget(
            "moving",
            new Rectangle(55, 0, 40, 20),
            frames);

        Assert.That(target, Is.EqualTo("target"));
    }
}
