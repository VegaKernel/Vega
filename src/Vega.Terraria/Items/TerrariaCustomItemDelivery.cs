using Multiplicity.Packets;
using Terraria.ID;
using Vega.Contracts.Players;
using Vega.Contracts.Items;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Items;

public sealed class TerrariaCustomItemDelivery(IGameThreadDispatcher dispatcher) : ICustomItemDelivery
{
	public ValueTask<PlayerOperationResult> DeliverAsync(
		PlayerId playerId,
		CustomItemDefinition item,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (item.Override.Width is not null || item.Override.Height is not null)
			return ValueTask.FromResult(PlayerOperationResult.Failed("custom_item.dimensions_unsupported"));
		if (cancellationToken.IsCancellationRequested)
			return ValueTask.FromResult(PlayerOperationResult.Cancelled());
		if (!dispatcher.IsGameThreadKnown)
			return ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
		if (dispatcher.IsGameThread)
			return ValueTask.FromResult(Deliver(playerId, item));

		var completion = new TaskCompletionSource<PlayerOperationResult>(
			TaskCreationOptions.RunContinuationsAsynchronously);
		if (!dispatcher.TryPost(
				"custom-item.deliver",
				() => completion.TrySetResult(
					cancellationToken.IsCancellationRequested
						? PlayerOperationResult.Cancelled()
						: Deliver(playerId, item))))
		{
			return ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
		}

		return new ValueTask<PlayerOperationResult>(completion.Task);
	}

	private static PlayerOperationResult Deliver(PlayerId playerId, CustomItemDefinition definition)
	{
		try
		{
			if ((uint)playerId.Slot >= (uint)global::Terraria.Main.maxPlayers ||
				global::Terraria.Main.player[playerId.Slot] is not { active: true } player ||
				global::Terraria.Netplay.Clients[playerId.Slot] is not { IsActive: true } client)
			{
				return PlayerOperationResult.NotFound();
			}

			int itemIndex = FindFreeItemSlot();
			if (itemIndex < 0)
				return PlayerOperationResult.Failed("custom_item.no_free_slot");

			global::Terraria.WorldItem worldItem = global::Terraria.Main.item[itemIndex];
			worldItem.inner.SetDefaults(definition.ItemType);
			ApplyOverride(worldItem.inner, definition.Override);
			worldItem.position = player.position;

			foreach (TerrariaPacket packet in CustomItemPacketFactory.Create(
					checked((short)itemIndex),
					checked((byte)playerId.Slot),
					player.position.X,
					player.position.Y,
					definition))
			{
				byte[] data = CustomItemPacketFactory.Serialize(packet);
				// This direct packet bypasses NetMessage's native send counter reservation.
				client.Socket.AsyncSend(data, 0, data.Length, static _ => { });
			}

			return PlayerOperationResult.Applied();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Vega custom-item delivery failed: {ex}");
			return PlayerOperationResult.Failed("custom_item.delivery_failed");
		}
	}

	private static int FindFreeItemSlot()
	{
		for (var index = 0; index < global::Terraria.Main.maxItems; index++)
		{
			if (!global::Terraria.Main.item[index].active &&
				global::Terraria.Main.timeItemSlotCannotBeReusedFor[index] == 0)
			{
				return index;
			}
		}

		return -1;
	}

	private static void ApplyOverride(global::Terraria.Item item, CustomItemOverride value)
	{
		if (value.Prefix is byte prefix) item.Prefix(prefix);
		if (value.Stack is short stack) item.stack = stack;
		if (value.Color is ItemColor color) item.color = new(color.Red, color.Green, color.Blue);
		if (value.Damage is ushort damage) item.damage = damage;
		if (value.Knockback is float knockback) item.knockBack = knockback;
		if (value.UseAnimation is ushort useAnimation) item.useAnimation = useAnimation;
		if (value.UseTime is ushort useTime) item.useTime = useTime;
		if (value.ShootProjectileType is short shoot) item.shoot = shoot;
		if (value.ShootSpeed is float shootSpeed) item.shootSpeed = shootSpeed;
		if (value.Scale is float scale) item.scale = scale;
		if (value.AmmoType is short ammo) item.ammo = ammo;
		if (value.UseAmmoType is short useAmmo) item.useAmmo = useAmmo;
		if (value.NotAmmo is bool notAmmo) item.notAmmo = notAmmo;
	}
}
