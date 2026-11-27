using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.Core.Networking;
using Vega.Regions;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class RegionBuildAuthorizationPolicyTests
{
    [Test]
    public async Task PluginTilePolicyObservesTypedIntentsAndOnlyRestrictsPreviouslyAuthorizedOperations()
    {
        var snapshot = new PlayerSnapshot(new(4), "Player", false, 100, 100, 20, 20, new(160, 160), false, 0)
        {
            SessionId = PlayerSessionId.New(),
            Connection = new(new(4), null, null, ConnectionForwardingProvenance.Direct, ClientPlatformInfo.Unknown, null, ConnectionLoginPhase.FullyConnected)
        };
        var players = new FakeDirectory(snapshot);
        var worldEvents = new WorldGameplayEventHub();
        var dispatcher = new TilePolicyThread();
        bool current = true;
        var pluginPolicy = new PlayerGameplayIntentPolicy(players, new FakeWorldInteractions(new(10, 10)), worldEvents,
            dispatcher, WorldId.Primary, p => current ? new CurrentPlayerIntentState(p.Position, p.IsHostile, p.Team) : null, new TilePolicyLogger());
        var seen = new List<PlayerTileInteractionEvent>();
        var accepted = new List<PlayerTileInteractionAcceptedEvent>();
        using var acceptance = worldEvents.Subscribe<PlayerTileInteractionAcceptedEvent>(accepted.Add);
        bool allow = true;
        await using var scope = new HotModuleRegistrationScope(new CommandDispatcher([]), new TilePolicyLogger(), "Fixture.RegionPolicy", Path.GetTempPath(), worldEvents: worldEvents);
        scope.WorldEvents.OnTileInteractionRequested(seen.Add);
        scope.WorldEvents.OnPlayerTileChangesCommitted(_ => { });
        scope.WorldEvents.AllowTileInteraction(_ => allow);
        var deniedBase = new PacketProcessorPipeline<PacketView>([CreatePolicy(Region()), pluginPolicy]);
        var allowedBase = new PacketProcessorPipeline<PacketView>([CreatePolicy(Region(), [RegionBuildAuthorizationPolicy.BypassPermission]), pluginPolicy]);
        var tile = new Tile { Action = TileAction.PlaceTile, TileX = 12, TileY = 14, Value = 1 };

        Assert.That(Process(deniedBase, tile).Reason, Is.EqualTo("packet.region.build_denied"));
        Assert.That(seen, Is.Empty, "Never expose rejected built-in operations to plugins.");
        allow = false;
        Assert.That(Process(allowedBase, tile).Reason, Is.EqualTo("packet.tile_policy.denied"));
        Assert.That(seen.Single(), Is.EqualTo(new PlayerTileInteractionEvent(WorldId.Primary, snapshot.Id, snapshot.SessionId!.Value,
            TileInteractionKind.Tile, TileInteractionAccess.Build, new(12, 14), new(12, 14), TileEditOperation.PlaceTile, 1)));

        Assert.That(accepted, Is.Empty, "Denied intents are never marked accepted.");
        allow = true;
        Assert.That(Process(allowedBase, tile).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(accepted.Single().Intent, Is.EqualTo(seen.Last()));
        current = false;
        Assert.That(Process(allowedBase, tile).Reason, Is.EqualTo("packet.tile_policy.session_unavailable"));
        Assert.That(seen, Has.Count.EqualTo(2));
        current = true;
        dispatcher.OnThread = false;
        Assert.That(Process(allowedBase, tile).Reason, Is.EqualTo("packet.tile_policy.session_unavailable"));
        Assert.That(seen, Has.Count.EqualTo(2));
        Assert.That(accepted, Has.Count.EqualTo(1));

        dispatcher.OnThread = true;
        var pvpSeen = new List<PlayerPvpChangeRequestedEvent>();
        scope.WorldEvents.OnPlayerPvpChangeRequested(pvpSeen.Add);
        scope.WorldEvents.AllowPlayerPvpChange(request => request.RequestedHostile != true);
        var pvp = new PacketProcessorPipeline<PacketView>([new PlayerPacketOwnershipPolicy(), pluginPolicy]);
        Assert.That(Process(pvp, new TogglePvp { PlayerId = 4, PvpEnabled = true }).Reason, Is.EqualTo("packet.pvp_policy.denied"));
        Assert.That(pvpSeen.Single(), Is.EqualTo(new PlayerPvpChangeRequestedEvent(WorldId.Primary, snapshot.Id, snapshot.SessionId!.Value,
            snapshot.Position, PlayerPvpChangeKind.Hostility, false, 0, true)));
        Assert.That(Process(pvp, new PlayerTeam { PlayerId = 4, Team = 2 }).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(pvpSeen[^1].RequestedTeam, Is.EqualTo(2));
        Assert.That(pvpSeen[^1].RequestedHostile, Is.Null);

        await scope.DisposeAsync();
        Assert.That(Process(allowedBase, tile).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(worldEvents.HasPolicies<PlayerTileInteractionEvent>(), Is.False);
        Assert.That(worldEvents.HasSubscribers<PlayerTileInteractionEvent>(), Is.False);
        Assert.That(worldEvents.HasSubscribers<PlayerTileChangesCommittedEvent>(), Is.False);
        Assert.That(worldEvents.HasPolicies<PlayerPvpChangeRequestedEvent>(), Is.False);
        Assert.That(worldEvents.HasSubscribers<PlayerPvpChangeRequestedEvent>(), Is.False);
    }

    private sealed class TilePolicyThread : IGameThreadDispatcher
    {
        public bool OnThread { get; set; } = true;
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => OnThread;
        public void AssertGameThread() => Assert.That(OnThread, Is.True);
        public bool TryPost(string name, Action action) => throw new NotSupportedException();
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }

    private sealed class TilePolicyLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
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
    public async Task WorldBuildProtectionIsDurableAndIndependentOfRegionPermissions()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-build-protection", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "build-protection.json");
        try
        {
            var authorization = new FakeAuthorization([RegionBuildAuthorizationPolicy.BypassPermission]);
            WorldTilePosition spawn = new(100, 100);
            var protection = new WorldBuildProtection(path, WorldId.Primary, authorization, () => spawn);
            await protection.ChangeAsync(s => s with { SpawnProtected = true, SpawnRadius = 10 });
            var reopened = new WorldBuildProtection(path, WorldId.Primary, authorization, () => spawn);
            var policy = new RegionBuildAuthorizationPolicy(new FakeRegions(Region()), new FakeDirectory(), authorization, worldProtection: reopened);
            Assert.That(Process(policy, new Tile { Action = TileAction.PlaceTile, TileX = 100, TileY = 100, Value = 1 }).Reason,
                Is.EqualTo("packet.build.spawn_protected"));
            Assert.That(Process(policy, new Tile { Action = TileAction.PlaceTile, TileX = 80, TileY = 80, Value = 1 }).Kind,
                Is.EqualTo(PacketDecisionKind.Allow));
            spawn = new(80, 80);
            Assert.That(Process(policy, new Tile { Action = TileAction.PlaceTile, TileX = 80, TileY = 80, Value = 1 }).Reason,
                Is.EqualTo("packet.build.spawn_protected"));
            Assert.That(Process(policy, new ChestOpen { ChestId = 1, ChestX = 80, ChestY = 80 }).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            await reopened.ChangeAsync(s => s with { GlobalDisabled = true });
            Assert.That(Process(policy, new Tile { Action = TileAction.PlaceTile, TileX = 200, TileY = 200, Value = 1 }).Reason,
                Is.EqualTo("packet.build.disabled"));
            var bypass = new WorldBuildProtection(path, WorldId.Primary,
                new FakeAuthorization([WorldBuildProtection.GlobalBypass, WorldBuildProtection.SpawnBypass]), () => spawn);
            Assert.That(bypass.Evaluate(WorldId.Primary, new(4), 80, 80, 80, 80).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(bypass.Evaluate(new("another"), new(4), 80, 80, 80, 80).Reason, Is.EqualTo("packet.build.wrong_world"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void AuthenticatedAccountIdentityOwnsRegionEvenWhenCharacterNameDiffers()
    {
        RegionSnapshot region = Region() with { Owner = "alice" };
        RegionBuildAuthorizationPolicy policy = new(
            new FakeRegions(region),
            new FakeDirectory(),
            new FakeAuthorization([]),
            new FakeAccountSessions());

        PacketProcessingDecision decision = Process(policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 12, TileY = 14, Value = 1 });

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void MassWireCrossingProtectedRegionIsRejected()
    {
        PacketProcessingDecision decision = Process(CreatePolicy(Region()),
            new MassWireOperation { StartX = 1, StartY = 14, EndX = 40, EndY = 14 });

        Assert.That(decision.Reason, Is.EqualTo("packet.region.build_denied"));
    }

    [Test]
    public void ProtectedRegionRejectsChestUseByDefault()
    {
        PacketProcessingDecision decision = Process(CreatePolicy(Region()),
            new ChestOpen { ChestId = 1, ChestX = 12, ChestY = 14 });

        Assert.That(decision.Reason, Is.EqualTo("packet.region.use_denied"));
    }

    [Test]
    public void PublicUseFlagAllowsChestUseWithoutOpeningBuild()
    {
        RegionSnapshot region = Region() with
        {
            Flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["protected"] = true,
                ["use"] = true
            }
        };
        RegionBuildAuthorizationPolicy policy = CreatePolicy(region);

        PacketProcessingDecision chest = Process(policy,
            new ChestGetContents { TileX = 12, TileY = 14 });
        PacketProcessingDecision build = Process(policy,
            new Tile { Action = TileAction.PlaceTile, TileX = 12, TileY = 14, Value = 1 });

        Assert.Multiple(() =>
        {
            Assert.That(chest.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(build.Reason, Is.EqualTo("packet.region.build_denied"));
        });
    }

    [Test]
    public void ChestItemUsesAuthoritativeChestPositionForRegionCheck()
    {
        RegionSnapshot region = Region();
        var interactions = new FakeWorldInteractions(new WorldTilePosition(12, 14));
        var policy = new RegionBuildAuthorizationPolicy(
            new FakeRegions(region),
            new FakeDirectory(),
            new FakeAuthorization([]),
            accountSessions: null,
            interactions);

        PacketProcessingDecision decision = Process(policy,
            new ChestItem { ChestId = 3, ItemSlot = 1, ItemNetId = 1 });

        Assert.That(decision.Reason, Is.EqualTo("packet.region.use_denied"));
    }

    [Test]
    public void TileEntityInteractionUsesAuthoritativePositionForRegionCheck()
    {
        RegionSnapshot region = Region();
        var interactions = new FakeWorldInteractions(new WorldTilePosition(12, 14));
        var policy = new RegionBuildAuthorizationPolicy(
            new FakeRegions(region),
            new FakeDirectory(),
            new FakeAuthorization([]),
            accountSessions: null,
            interactions);

        PacketProcessingDecision decision = Process(policy,
            new RequestTileEntityInteraction { TileEntityId = 8, PlayerId = 4 });

        Assert.That(decision.Reason, Is.EqualTo("packet.region.build_denied"));
    }

    private static RegionBuildAuthorizationPolicy CreatePolicy(
        RegionSnapshot region,
        IEnumerable<string>? permissions = null) =>
        new(new FakeRegions(region), new FakeDirectory(), new FakeAuthorization(permissions ?? []));

    private static PacketProcessingDecision Process(
        IPacketProcessor<PacketView> policy,
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

    private sealed class FakeDirectory(PlayerSnapshot? snapshot = null) : IPlayerDirectory
    {
        private readonly PlayerSnapshot Player = snapshot ?? new(
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

    [Test]
    public void AccountScopedRegionMembershipRequiresAuthenticatedSession()
    {
        var region = Region() with { Members = Set("Player", "alice") };
        var unauthenticated = new RegionBuildAuthorizationPolicy(new FakeRegions(region), new FakeDirectory(), new FakeAuthorization([]), new FakeAccountSessions(false));
        var authenticated = new RegionBuildAuthorizationPolicy(new FakeRegions(region), new FakeDirectory(), new FakeAuthorization([]), new FakeAccountSessions());
        var operation = new Tile { Action = TileAction.PlaceTile, TileX = 12, TileY = 14, Value = 1 };
        Assert.That(Process(unauthenticated, operation).Kind, Is.EqualTo(PacketDecisionKind.Reject));
        Assert.That(Process(authenticated, operation).Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    private sealed class FakeAccountSessions(bool authenticated = true) : IPlayerAccountSessionService
    {
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account)
        {
            account = authenticated ? new AccountSnapshot(new AccountId(1), "alice", "default", null) { Revision = 1 } : null;
            return authenticated;
        }

        public ValueTask<bool> SetCultureAsync(
            PlayerId playerId,
            string? culture,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    private sealed class FakeWorldInteractions(WorldTilePosition chestPosition) : IWorldInteractionState
    {
        public bool TryGetActiveChest(PlayerId playerId, out int chestId)
        {
            chestId = 3;
            return true;
        }

        public bool TryGetChestPosition(int chestId, out WorldTilePosition position)
        {
            position = chestPosition;
            return chestId == 3;
        }

        public bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position)
        {
            position = chestPosition;
            return tileEntityId == 8;
        }
    }
}
