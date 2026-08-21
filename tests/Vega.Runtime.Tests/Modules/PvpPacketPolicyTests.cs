using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Extensions;
using Vega.Modules.Pvp;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class PvpPacketPolicyTests
{
	[Test]
	public void HostilePlayerCannotAuthorHealth()
	{
		using TestPolicy test = CreatePolicy(isHostile: true);
		var packet = new PlayerHp { PlayerId = 7, Hp = 100, MaxHp = 100 };

		PacketProcessingDecision decision = test.Process(packet);

		Assert.That(decision, Is.EqualTo(
			new PacketProcessingDecision(PacketDecisionKind.Reject, "pvp.health_server_authoritative")));
	}

	[Test]
	public void ProjectileWeaponAttributionRejectsAChangedDamageSource()
	{
		using TestPolicy test = CreatePolicy(heldItemType: 39);
		PacketProcessingDecision projectile = test.Process(new ProjectileNew
		{
			Key = new ProjectileKey(7, 42, 0),
			Type = 1
		});
		PacketProcessingDecision damage = test.Process(new PlayerHurtV2
		{
			PlayerId = 8,
			FromPlayerIndex = 7,
			FromProjectileIndex = 42,
			FromProjectileType = 1,
			FromItemType = 40,
			Damage = 20
		});

		Assert.Multiple(() =>
		{
			Assert.That(projectile.Kind, Is.EqualTo(PacketDecisionKind.Continue));
			Assert.That(damage, Is.EqualTo(
				new PacketProcessingDecision(PacketDecisionKind.Reject, "pvp.damage_weapon_mismatch")));
		});
	}

	[Test]
	public void DeathWindowStopsLateProjectiles()
	{
		using TestPolicy test = CreatePolicy();
		test.Process(new PlayerDeathV2 { PlayerId = 7, Damage = 100 });

		PacketProcessingDecision decision = test.Process(
			new ProjectileNew { Key = new ProjectileKey(7, 5, 0), Type = 1 },
			TimeSpan.FromSeconds(1));

		Assert.That(decision, Is.EqualTo(
			new PacketProcessingDecision(PacketDecisionKind.Reject, "pvp.projectile_while_inactive")));
	}

	private static TestPolicy CreatePolicy(
		bool isHostile = false,
		short heldItemType = 1)
	{
		var player = new PlayerSnapshot(
			new PlayerId(7), "Alice", false, 100, 100, 20, 20,
			new PlayerPosition(0, 0), isHostile, 0);
		return new TestPolicy(player, heldItemType);
	}

	private sealed class TestPolicy : IDisposable, IPlayerDirectory, IPlayerCombatDirectory, IPlayerSessionLifecycle
	{
		private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");
		private readonly PlayerSnapshot player;
		private readonly PlayerCombatSnapshot combat;
		private readonly ExtensionStateStore store = new();
		private readonly PvpSessionStateService sessions;
		private readonly PvpPacketPolicy policy;

		public TestPolicy(PlayerSnapshot player, short heldItemType)
		{
			this.player = player;
			combat = new PlayerCombatSnapshot(player.Id, heldItemType, 0, false);
			sessions = new PvpSessionStateService(store, this);
			policy = new PvpPacketPolicy(this, this, sessions);
			Opened?.Invoke(new PlayerSessionOpened(PlayerSessionId.New(), player.Id, player.Name, Now));
		}

		public event Action<PlayerSessionOpened>? Opened;
		public event Action<PlayerSessionClosed>? Closed;

		public PacketProcessingDecision Process(TerrariaPacket value, TimeSpan? offset = null)
		{
			using var stream = new MemoryStream();
			value.ToStream(stream, includeHeader: false);
			var packet = PacketView.FromPayload(value.PacketType, stream.ToArray());
			var context = new PacketProcessingContext<PacketView>(
				packet,
				PacketDirection.Inbound,
				new ConnectionId(player.Id.Slot),
				player.Id,
				Now + (offset ?? TimeSpan.Zero));
			return policy.Process(in context);
		}

		public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => [player];
		public bool TryGet(PlayerId id, out PlayerSnapshot? snapshot)
		{
			snapshot = id == player.Id ? player : null;
			return snapshot is not null;
		}
		public bool TryFindExact(string name, out PlayerSnapshot? snapshot)
		{
			snapshot = string.Equals(name, player.Name, StringComparison.OrdinalIgnoreCase) ? player : null;
			return snapshot is not null;
		}
		public bool TryGetCombatSnapshot(PlayerId id, out PlayerCombatSnapshot? snapshot)
		{
			snapshot = id == player.Id ? combat : null;
			return snapshot is not null;
		}

		public void Dispose()
		{
			Closed?.Invoke(new PlayerSessionClosed(PlayerSessionId.New(), player.Id, player.Name, Now));
			sessions.Dispose();
			store.Dispose();
		}
	}
}
