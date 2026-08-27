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
