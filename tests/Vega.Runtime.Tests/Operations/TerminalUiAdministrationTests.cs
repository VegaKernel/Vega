using NUnit.Framework;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.TerminalUI;

namespace Vega.Runtime.Tests.Operations;

[TestFixture]
public sealed class TerminalUiAdministrationTests
{
    [Test]
    public void FixedLayoutRemovesMovementAndOverlapButKeepsResize()
    {
        var desktop = new View();
        var frame = new FrameView
        {
            Arrangement = ViewArrangement.Movable | ViewArrangement.Resizable | ViewArrangement.Overlapped
        };
        desktop.Add(frame);

        TerminalUiFixedLayout.Apply(desktop);

        Assert.That(frame.Arrangement, Is.EqualTo(ViewArrangement.Resizable));
    }

    [Test]
    public void AdministrationRowsExposeGroupsPermissionsAccountsAndPossibleLinksWithoutRawIdentityValues()
    {
        DateTimeOffset matchedAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        var snapshot = new AdministrationOperationsSnapshot(
            [new PermissionGroupOperationsSnapshot(
                "administrator",
                "default",
                ["vega.*"],
                ["vega.world.destroy"],
                1,
                3)],
            [new AccountOperationsSnapshot(
                new AccountId(1),
                "alice",
                "administrator",
                matchedAt,
                "ru-RU",
                [new PossibleAccountOperationsSnapshot(
                    "bob",
                    2,
                    SharedClientUuid: true,
                    SharedIpAddress: true,
                    matchedAt)])],
            matchedAt);

        string[] rows = AdministrationBrowserRows.Build(snapshot);
        string[] linkSearch = AdministrationBrowserRows.Filter(rows, "alice bob UUID+IP");
        string[] permissionSearch = AdministrationBrowserRows.Filter(rows, "DENY destroy");

        Assert.Multiple(() =>
        {
            Assert.That(rows.Any(row => row.Contains("GROUP   administrator", StringComparison.Ordinal)), Is.True);
            Assert.That(rows.Any(row => row.Contains("GRANT administrator · vega.*", StringComparison.Ordinal)), Is.True);
            Assert.That(rows.Any(row => row.Contains("ACCOUNT alice", StringComparison.Ordinal)), Is.True);
            Assert.That(linkSearch, Has.Length.EqualTo(1));
            Assert.That(linkSearch[0], Does.Contain("UUID+IP"));
            Assert.That(permissionSearch, Has.Length.EqualTo(1));
            Assert.That(rows.Any(row => row.Contains("203.0.113", StringComparison.Ordinal)), Is.False);
            Assert.That(rows.Any(row => row.Contains("device-", StringComparison.Ordinal)), Is.False);
        });
    }
}
