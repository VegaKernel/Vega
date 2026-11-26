using Multiplicity.Packets;
using Terraria.DataStructures;
using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Items;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerBoundary
{
    public ValueTask<PlayerOperationResult> ResynchronizeAreaAsync(PlayerSnapshot player, int radius, CancellationToken cancellationToken = default) =>
        DispatchAsync("player.sync", () =>
        {
            if (!TryGetSessionPlayer(player, out var live)) return PlayerOperationResult.NotFound();
            if (radius is < 1 or > 16) return PlayerOperationResult.Failed("player.sync_radius");
            int x = (int)(live.Center.X / 16), y = (int)(live.Center.Y / 16);
            int minX = Math.Max(0, x - radius), minY = Math.Max(0, y - radius);
            int maxX = Math.Min(global::Terraria.Main.maxTilesX - 1, x + radius), maxY = Math.Min(global::Terraria.Main.maxTilesY - 1, y + radius);
            if (minX > maxX || minY > maxY) return PlayerOperationResult.Failed("player.sync_position");
            // Limit each native tile packet to a small square, including dense tile data.
            for (int tx = minX; tx <= maxX; tx += 16)
                for (int ty = minY; ty <= maxY; ty += 16)
                    global::Terraria.NetMessage.SendTileSquare(live.whoAmI, tx, ty, Math.Min(16, maxX - tx + 1), Math.Min(16, maxY - ty + 1));
            return PlayerOperationResult.Applied();
        }, "player.sync_failed", cancellationToken);
	private (int Id, string Name)[]? itemCatalog;
	private (int Id, string Name)[]? buffCatalog;
	private IContentRestrictionPolicy? contentRestrictions;

	public void ConfigureContentRestrictions(IContentRestrictionPolicy policy) => contentRestrictions = policy;

	public bool TryGetItemType(PlayerId player, int slot, out int type)
	{
		if (TryGetActivePlayer(player, out var live) && (uint)slot < (uint)live.inventory.Length)
		{
			type = live.inventory[slot].type;
			return true;
		}
		type = 0;
		return false;
	}

	public ValueTask<PlayerOperationResult> GiveAsync(PlayerSnapshot player, string item, int stack, int prefix, CancellationToken cancellationToken = default) =>
		DispatchAsync("player.give-item", () =>
		{
			if (!TryGetSessionPlayer(player, out var live) || live.dead) return PlayerOperationResult.NotFound();
			if (stack <= 0 || prefix < 0 || prefix >= PrefixID.Count || string.IsNullOrWhiteSpace(item))
				return PlayerOperationResult.Failed("item.invalid_request");
			int type;
			if (!int.TryParse(item, out type))
			{
				itemCatalog ??= Enumerable.Range(1, ItemID.Count - 1).Select(id => (id, global::Terraria.Lang.GetItemNameValue(id))).ToArray();
				var matches = itemCatalog.Where(entry => string.Equals(entry.Name, item, StringComparison.OrdinalIgnoreCase)).ToArray();
				if (matches.Length == 0) matches = itemCatalog.Where(entry => entry.Name.StartsWith(item, StringComparison.OrdinalIgnoreCase)).ToArray();
				if (matches.Length != 1) return PlayerOperationResult.Failed("item.not_unique");
				type = matches[0].Id;
			}
			if (type <= 0 || type >= ItemID.Count) return PlayerOperationResult.Failed("item.invalid_type");
			if (contentRestrictions?.IsAllowed(player.Id, RestrictedContentKind.Item, type) == false)
				return PlayerOperationResult.Failed("restriction.item_give");
			var template = new global::Terraria.Item();
			template.SetDefaults(type);
			if (stack > template.maxStack || (prefix != 0 && !template.Prefix(prefix))) return PlayerOperationResult.Failed("item.invalid_stack_or_prefix");
			bool vacant = false;
			for (int i = 0; i < global::Terraria.Main.maxItems; i++)
			{
				if (!global::Terraria.Main.item[i].active && global::Terraria.Main.timeItemSlotCannotBeReusedFor[i] == 0) { vacant = true; break; }
			}
			if (!vacant) return PlayerOperationResult.Failed("item.no_free_slot");
			int slot = global::Terraria.Item.NewItem(new EntitySource_DebugCommand(), live.Center,
				type, stack, prefix, global::Terraria.NewItemOwnership.None, noBroadcast: true);
			if ((uint)slot >= (uint)global::Terraria.Main.maxItems || !global::Terraria.Main.item[slot].active)
				return PlayerOperationResult.Failed("item.spawn_failed");
			global::Terraria.Main.item[slot].playerIndexTheItemIsReservedFor = live.whoAmI;
			global::Terraria.NetMessage.SendData(MessageID.SyncItem, live.whoAmI, -1, null, slot, 1);
			global::Terraria.NetMessage.SendData(MessageID.ItemOwner, live.whoAmI, -1, null, slot);
			return PlayerOperationResult.Applied();
		}, "item.delivery_failed", cancellationToken);

	public ValueTask<PlayerOperationResult> HealAsync(PlayerSnapshot player, int amount, CancellationToken cancellationToken = default) =>
		DispatchAsync("player.heal", () =>
		{
			if (!TryGetSessionPlayer(player, out var live) || live.dead) return PlayerOperationResult.NotFound();
			if (amount <= 0) return PlayerOperationResult.Failed("heal.invalid_amount");
			int applied = Math.Min(amount, Math.Max(0, live.statLifeMax2 - live.statLife));
			live.statLife += applied;
			if (applied > 0) global::Terraria.NetMessage.SendData((int)PacketTypes.PlayerHealOther, -1, -1, null, live.whoAmI, applied);
			return PlayerOperationResult.Applied();
		}, "heal.failed", cancellationToken);

	public ValueTask<PlayerOperationResult> BuffAsync(PlayerSnapshot player, string buff, int seconds, CancellationToken cancellationToken = default) =>
		DispatchAsync("player.buff", () =>
		{
			if (!TryGetSessionPlayer(player, out var live) || live.dead) return PlayerOperationResult.NotFound();
			if (seconds is < 1 or > 3600 || string.IsNullOrWhiteSpace(buff)) return PlayerOperationResult.Failed("buff.invalid_duration");
			int type;
			if (!int.TryParse(buff, out type))
			{
				buffCatalog ??= Enumerable.Range(1, BuffID.Count - 1).Select(id => (id, global::Terraria.Lang.GetBuffName(id))).ToArray();
				var matches = buffCatalog.Where(entry => string.Equals(entry.Name, buff, StringComparison.OrdinalIgnoreCase)).ToArray();
				if (matches.Length == 0) matches = buffCatalog.Where(entry => entry.Name.StartsWith(buff, StringComparison.OrdinalIgnoreCase)).ToArray();
				if (matches.Length != 1) return PlayerOperationResult.Failed("buff.not_unique");
				type = matches[0].Id;
			}
			if (type <= 0 || type >= BuffID.Count) return PlayerOperationResult.Failed("buff.invalid_type");
			int ticks = seconds * 60;
			live.AddBuff(type, ticks);
			global::Terraria.NetMessage.SendData((int)PacketTypes.PlayerAddBuff, live.whoAmI, -1, null, live.whoAmI, type, ticks);
			return PlayerOperationResult.Applied();
		}, "buff.failed", cancellationToken);

	// These native requests are sent to the owning client only. Vanilla confirms
	// the transition through its normal incoming player-state handling.
	public ValueTask<PlayerOperationResult> KillAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
		DispatchAsync("player.kill", () =>
		{
			if (!TryGetSessionPlayer(player, out var live)) return PlayerOperationResult.NotFound();
			if (live.dead || live.ghost) return PlayerOperationResult.Failed("kill.already_dead");
			global::Terraria.NetMessage.SendPlayerDeath(live.whoAmI, PlayerDeathReason.LegacyDefault(), short.MaxValue, 0,
				pvp: false, remoteClient: live.whoAmI);
			return PlayerOperationResult.Applied();
		}, "kill.failed", cancellationToken);

	public ValueTask<PlayerOperationResult> SlapAsync(PlayerSnapshot player, int damage, CancellationToken cancellationToken = default) =>
		DispatchAsync("player.slap", () =>
		{
			if (!TryGetSessionPlayer(player, out var live)) return PlayerOperationResult.NotFound();
			if (live.dead || live.ghost) return PlayerOperationResult.Failed("slap.already_dead");
			if (damage < 0 || damage > short.MaxValue) return PlayerOperationResult.Failed("slap.invalid_damage");
			global::Terraria.NetMessage.SendPlayerHurt(live.whoAmI, PlayerDeathReason.LegacyDefault(), damage, 0,
				critical: false, pvp: false, hitContext: 0, dodgeable: false, remoteClient: live.whoAmI);
			return PlayerOperationResult.Applied();
		}, "slap.failed", cancellationToken);

	public ValueTask<PlayerOperationResult> RespawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
		DispatchAsync("player.respawn", () =>
		{
			if (!TryGetSessionPlayer(player, out var live)) return PlayerOperationResult.NotFound();
			if (!live.dead) return PlayerOperationResult.Failed("respawn.not_dead");
			if (live.ghost) return PlayerOperationResult.Failed("respawn.ghost");
			global::Terraria.NetMessage.SendData(MessageID.PlayerSpawn, live.whoAmI, -1, null,
				live.whoAmI, (int)global::Terraria.PlayerSpawnContext.ReviveFromDeath);
			return PlayerOperationResult.Applied();
		}, "respawn.failed", cancellationToken);

}
