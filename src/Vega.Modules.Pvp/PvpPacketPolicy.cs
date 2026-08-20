using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Modules.Pvp;

public sealed class PvpPacketPolicy(
	IPlayerDirectory players,
	IPlayerCombatDirectory combat,
	PvpSessionStateService sessions) : IPacketProcessor<PacketView>
{
	private static readonly TimeSpan DeathWindow = TimeSpan.FromSeconds(5);

	public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
	{
		if (context.Direction != PacketDirection.Inbound || context.PlayerId is not PlayerId sender)
		{
			return Continue();
		}

		return context.Packet.PacketType switch
		{
			PacketTypes.PlayerHp => ProcessHealth(sender),
			PacketTypes.ProjectileNew => ProcessProjectile(sender, context.Packet, context.Timestamp),
			PacketTypes.PlayerHurtV2 => ProcessDamage(sender, context.Packet),
			PacketTypes.PlayerDeathV2 => ProcessDeath(sender, context.Packet, context.Timestamp),
			_ => Continue()
		};
	}

	private PacketProcessingDecision ProcessHealth(PlayerId sender)
	{
		return players.TryGet(sender, out PlayerSnapshot? player) && player is { IsHostile: true }
			? Reject("pvp.health_server_authoritative")
			: Continue();
	}

	private PacketProcessingDecision ProcessProjectile(
		PlayerId sender,
		PacketView packet,
		DateTimeOffset timestamp)
	{
		var projectile = new ProjectileNewView(packet);
		if (projectile.Owner != sender.Slot)
		{
			return Reject("pvp.projectile_owner_mismatch");
		}

		if (!players.TryGet(sender, out PlayerSnapshot? player) || player is null)
		{
			return Reject("pvp.player_not_found");
		}

		if (player.IsDead || sessions.IsSpectating(sender) || sessions.IsInsideDeathWindow(sender, timestamp))
		{
			return Reject("pvp.projectile_while_inactive");
		}

		if (combat.TryGetCombatSnapshot(sender, out PlayerCombatSnapshot? heldItem) &&
			heldItem is { HeldItemType: > 0 })
		{
			sessions.TryRecordProjectileWeaponType(
				sender,
				projectile.Type,
				heldItem.HeldItemType,
				timestamp);
		}

		return Continue();
	}

	private PacketProcessingDecision ProcessDamage(PlayerId sender, PacketView packet)
	{
		var damage = new PlayerHurtV2View(packet);
		if (damage.FromPlayerIndex >= 0 && damage.FromPlayerIndex != sender.Slot)
		{
			return Reject("pvp.damage_attacker_mismatch");
		}

		if (damage.FromProjectileType > 0 &&
			damage.FromItemType > 0 &&
			sessions.TryGetProjectileWeaponType(sender, damage.FromProjectileType, out var attribution) &&
			attribution is not null &&
			damage.FromItemType != attribution.WeaponItemType)
		{
			return Reject("pvp.damage_weapon_mismatch");
		}

		return Continue();
	}

	private PacketProcessingDecision ProcessDeath(
		PlayerId sender,
		PacketView packet,
		DateTimeOffset timestamp)
	{
		var death = new PlayerDeathV2View(packet);
		if (death.PlayerId != sender.Slot)
		{
			return Reject("pvp.death_player_mismatch");
		}

		sessions.TryRecordDeath(sender, timestamp, DeathWindow);
		return Continue();
	}

	private static PacketProcessingDecision Continue() =>
		new(PacketDecisionKind.Continue);

	private static PacketProcessingDecision Reject(string reason) =>
		new(PacketDecisionKind.Reject, reason);
}
