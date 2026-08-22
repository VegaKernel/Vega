using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class RegionBuildAuthorizationPolicyTests
{
    [Test]
    public void ProtectedRegionRejectsUnknownPlayer()
    {
        RegionBuildAuthorizationPolicy policy = CreatePolicy(Region());

        PacketProcessingDecision decision = Process(
            policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 12, TileY = 14, Value = 1 });

        Assert.That(decision.Reason, Is.EqualTo("packet.region.build_denied"));
    }

    [TestCase("owner")]
    [TestCase("member")]
    [TestCase("group")]
    [TestCase("bypass")]
    public void RegionAccessAllowsAuthorizedPlayer(string access)
    {
        RegionSnapshot region = Region() with
        {
            Owner = access == "owner" ? "Player" : null,
            Members = access == "member" ? Set("Player") : Set(),
            Groups = access == "group" ? Set("builders") : Set()
        };
        string[] permissions = access switch
        {
            "group" => ["vega.group.builders"],
            "bypass" => [RegionBuildAuthorizationPolicy.BypassPermission],
            _ => []
        };
        RegionBuildAuthorizationPolicy policy = CreatePolicy(region, permissions);

        PacketProcessingDecision decision = Process(
            policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 12, TileY = 14, Value = 1 });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [TestCase("protected")]
    [TestCase("build")]
    public void ExplicitPublicFlagAllowsBuilding(string flag)
    {
        RegionSnapshot region = Region() with
        {
            Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [flag] = flag == "build"
            }
        };

        PacketProcessingDecision decision = Process(CreatePolicy(region),
            new PlaceTileEntity { X = 15, Y = 15 });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void MutationOutsideRegionsIsAllowed()
    {
        PacketProcessingDecision decision = Process(CreatePolicy(Region()),
            new PlaceChest { TileX = 80, TileY = 80 });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void MassWireCrossingProtectedRegionIsRejected()
    {
        PacketProcessingDecision decision = Process(CreatePolicy(Region()),
            new MassWireOperation { StartX = 1, StartY = 1, EndX = 12, EndY = 14 });

        Assert.That(decision.Reason, Is.EqualTo("packet.region.build_denied"));
    }

    private static RegionBuildAuthorizationPolicy CreatePolicy(
        RegionSnapshot region,
        IEnumerable<string>? permissions = null) =>
        new(new FakeRegions(region), new FakeDirectory(), new FakeAuthorization(permissions ?? []));

    private static PacketProcessingDecision Process(
        RegionBuildAuthorizationPolicy policy,
        TerrariaPacket value)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(4),
            new PlayerId(4),
            DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private static RegionSnapshot Region() =>
        new(1, "spawn", 10, 10, 20, 20, null)
        {
            WorldId = WorldId.Primary,
            Priority = 10,
            Members = Set(),
            Groups = Set(),
            Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["protected"] = true
            },
            Revision = 1
        };

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    private sealed class FakeAuthorization(IEnumerable<string> permissions) : IPlayerAuthorizationService
    {
        private readonly HashSet<string> granted = new(permissions, StringComparer.OrdinalIgnoreCase);

        public bool HasPermission(PlayerId playerId, string permission) => granted.Contains(permission);
    }

    private sealed class FakeDirectory : IPlayerDirectory
    {
        private static readonly PlayerSnapshot Player = new(
            new PlayerId(4),
            "Player",
            false,
            100,
            100,
            20,
            20,
            new PlayerPosition(16, 16),
            false,
            0);

        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [Player];

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = id == Player.Id ? Player : null;
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = string.Equals(name, Player.Name, StringComparison.OrdinalIgnoreCase) ? Player : null;
            return player is not null;
        }
    }

    private sealed class FakeRegions(RegionSnapshot region) : IRegionService
    {
        public IReadOnlyList<RegionSnapshot> SnapshotRegions() => [region];

        public RegionSnapshot? FindTopRegion(int tileX, int tileY) =>
            FindTopRegion(WorldId.Primary, tileX, tileY);

        public IReadOnlyList<RegionSnapshot> SnapshotRegions(WorldId worldId) =>
            worldId == region.WorldId ? [region] : [];

        public RegionSnapshot? FindTopRegion(WorldId worldId, int tileX, int tileY) =>
            worldId == region.WorldId && region.Contains(tileX, tileY) ? region : null;

        public ValueTask<RegionMutationResult> CreateAsync(
            WorldId worldId, string name, int x, int y, int width, int height, int priority,
            string? owner, string actor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RegionMutationResult> DeleteAsync(
            WorldId worldId, string name, long expectedRevision, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RegionMutationResult> UpdateAsync(
            WorldId worldId, string name, RegionUpdate update, long expectedRevision, string actor,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public ValueTask<RegionMutationResult> SetAccessAsync(
            WorldId worldId, string name, RegionAccessKind kind, string subject, bool enabled,
            long expectedRevision, string actor, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
