using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Core.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class GameplayEnforcementPolicyTests
{
    [Test]
    public void PublishedSecurityConfigurationDeserializesWithDimensionsSettings()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "config", "security.json");

        GameplayEnforcementOptions options = GameplayEnforcementOptionsStore.Load(path, new RecordingLogger());

        Assert.Multiple(() =>
        {
            Assert.That(options.EnableDimensionsPacket, Is.False);
            Assert.That(options.DimensionsTrustedProxies, Is.EquivalentTo(new[] { "127.0.0.1" }));
        });
    }

    [Test]
    public void DisabledConfigurationDoesNotChangeVanillaGameplay()
    {
        using GameplayEnforcementPolicy policy = CreatePolicy(
            GameplayEnforcementOptions.Default,
            Player(dead: false));

        PacketProcessingDecision decision = Process(
            policy,
            new PlayerUpdate
            {
                PlayerId = 4,
                MovementFlags = UpdatePlayerMovementFlags.IsGhost
            },
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void EnabledConfigurationRejectsLivingGhostAndSpectate()
    {
        GameplayEnforcementOptions options = GameplayEnforcementOptions.Default with { Enabled = true };
        using GameplayEnforcementPolicy policy = CreatePolicy(options, Player(dead: false));

        PacketProcessingDecision ghost = Process(
            policy,
            new PlayerUpdate
            {
                PlayerId = 4,
                MovementFlags = UpdatePlayerMovementFlags.IsGhost
            },
            DateTimeOffset.UtcNow);
        PacketProcessingDecision spectate = Process(
            policy,
            new SpectatePlayer { PlayerId = 4, TargetPlayerId = 2 },
            DateTimeOffset.UtcNow);

        Assert.Multiple(() =>
        {
            Assert.That(ghost.Reason, Is.EqualTo("packet.gameplay.living_ghost"));
            Assert.That(spectate.Reason, Is.EqualTo("packet.gameplay.spectate_while_alive"));
        });
    }

    [Test]
    public void PermissionBypassesConfiguredGameplayRules()
    {
        GameplayEnforcementOptions options = GameplayEnforcementOptions.Default with { Enabled = true };
        using GameplayEnforcementPolicy policy = CreatePolicy(options, Player(dead: false), bypass: true);

        PacketProcessingDecision decision = Process(
            policy,
            new PlayerUpdate
            {
                PlayerId = 4,
                MovementFlags = UpdatePlayerMovementFlags.IsGhost
            },
            DateTimeOffset.UtcNow);

        Assert.That(decision.Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void RepeatedDeathAndRemoteWorldMutationAreRejected()
    {
        GameplayEnforcementOptions options = GameplayEnforcementOptions.Default with { Enabled = true };
        using GameplayEnforcementPolicy policy = CreatePolicy(options, Player(dead: true));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        PacketProcessingDecision firstDeath = Process(
            policy,
            new PlayerDeathV2 { PlayerId = 4, Damage = 100 },
            now);
        PacketProcessingDecision repeatedDeath = Process(
            policy,
            new PlayerDeathV2 { PlayerId = 4, Damage = 100 },
            now.AddMilliseconds(100));
        PacketProcessingDecision remoteChest = Process(
            policy,
            new PlaceChest { TileX = 100, TileY = 100 },
            now);

        Assert.Multiple(() =>
        {
            Assert.That(firstDeath.Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(repeatedDeath.Reason, Is.EqualTo("packet.gameplay.repeated_death"));
            Assert.That(remoteChest.Reason, Is.EqualTo("packet.gameplay.interaction_too_far"));
        });
    }

    [Test]
    public void OptionsAreBoundedAndBlankPermissionFallsBack()
    {
        GameplayEnforcementOptions normalized = GameplayEnforcementOptions.Normalize(
            new GameplayEnforcementOptions(true, true, true, -1, 999, " "));

        Assert.Multiple(() =>
        {
            Assert.That(normalized.MinimumDeathIntervalMilliseconds, Is.Zero);
            Assert.That(normalized.MaximumInteractionDistanceTiles, Is.EqualTo(200));
            Assert.That(normalized.BypassPermission,
                Is.EqualTo(GameplayEnforcementOptions.Default.BypassPermission));
        });
    }

    private static GameplayEnforcementPolicy CreatePolicy(
        GameplayEnforcementOptions options,
        PlayerSnapshot player,
        bool bypass = false)
    {
        var sessions = new FakeSessions();
        return new GameplayEnforcementPolicy(
            options,
            new FakeAuthorization(bypass),
            new FakeDirectory(player),
            sessions);
    }

    private static PacketProcessingDecision Process(
        GameplayEnforcementPolicy policy,
        TerrariaPacket value,
        DateTimeOffset timestamp)
    {
        using var stream = new MemoryStream();
        value.ToStream(stream, includeHeader: false);
        PacketView packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(
            packet,
            PacketDirection.Inbound,
            new ConnectionId(4),
            new PlayerId(4),
            timestamp);
        return policy.Process(in context);
    }

    private static PlayerSnapshot Player(bool dead) =>
        new(
            new PlayerId(4),
            "Player",
            dead,
            100,
            100,
            20,
            20,
            new PlayerPosition(16, 16),
            false,
            0);

    private sealed class FakeAuthorization(bool bypass) : IPlayerAuthorizationService
    {
        public bool HasPermission(PlayerId playerId, string permission) => bypass;
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }

    private sealed class FakeDirectory(PlayerSnapshot player) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];

        public bool TryGet(PlayerId id, out PlayerSnapshot? result)
        {
            result = id == player.Id ? player : null;
            return result is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? result)
        {
            result = string.Equals(name, player.Name, StringComparison.OrdinalIgnoreCase) ? player : null;
            return result is not null;
        }
    }

    private sealed class FakeSessions : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened
        {
            add { }
            remove { }
        }

        public event Action<PlayerSessionClosed>? Closed
        {
            add { }
            remove { }
        }
    }
}
