using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class GlobalBuildProtectionPolicyTests
{
    [Test]
    public void EnabledProtectionRejectsBuildPacketsButAllowsUsePackets()
    {
        var state = new RuntimeWorldBuildProtectionService();
        state.Toggle();
        var policy = new GlobalBuildProtectionPolicy(state, new Authorization(false));

        PacketProcessingDecision tile = Process(policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 10, TileY = 20, Value = 1 });
        PacketProcessingDecision playerUpdate = Process(policy,
            new PlayerUpdate { PlayerId = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(tile.Reason, Is.EqualTo("packet.antibuild.build_denied"));
            Assert.That(playerUpdate.Kind, Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public void DisabledProtectionOrPermissionAllowsBuild(bool enabled, bool bypass)
    {
        var state = new RuntimeWorldBuildProtectionService();
        if (enabled) state.Toggle();
        var policy = new GlobalBuildProtectionPolicy(state, new Authorization(bypass));

        PacketProcessingDecision decision = Process(policy,
            new PlaceChest { TileX = 10, TileY = 20 });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void SpawnProtectionRejectsInsideCircularRadiusAndAllowsOutside()
    {
        var state = new RuntimeWorldBuildProtectionService();
        state.ToggleSpawnProtection(100, 50, 10);
        var policy = new GlobalBuildProtectionPolicy(state, new Authorization(false));

        PacketProcessingDecision inside = Process(policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 106, TileY = 58, Value = 1 });
        PacketProcessingDecision outside = Process(policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 108, TileY = 58, Value = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(inside.Reason, Is.EqualTo("packet.spawn_protection.build_denied"));
            Assert.That(outside.Kind, Is.EqualTo(PacketDecisionKind.Allow));
        });
    }

    [Test]
    public void EditSpawnPermissionBypassesSpawnProtection()
    {
        var state = new RuntimeWorldBuildProtectionService();
        state.ToggleSpawnProtection(100, 50, 10);
        var policy = new GlobalBuildProtectionPolicy(state, new Authorization(true));

        PacketProcessingDecision decision = Process(policy,
            new PaintTile { X = 100, Y = 50, Color = 1 });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void MovingSpawnUpdatesProtectedAreaWithoutRetoggling()
    {
        var state = new RuntimeWorldBuildProtectionService();
        state.ToggleSpawnProtection(10, 10, 10);
        state.UpdateSpawnPosition(200, 100);

        Assert.Multiple(() =>
        {
            Assert.That(state.IsSpawnProtected(10, 10), Is.False);
            Assert.That(state.IsSpawnProtected(200, 100), Is.True);
        });
    }

    private static PacketProcessingDecision Process(
        GlobalBuildProtectionPolicy policy,
        TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(1),
            new PlayerId(1),
            DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class Authorization(bool bypass) : IPlayerAuthorizationService
    {
        public bool HasPermission(PlayerId playerId, string permission) => bypass;
    }
}