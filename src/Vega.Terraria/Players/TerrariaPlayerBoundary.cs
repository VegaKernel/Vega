using Microsoft.Xna.Framework;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using TShockNG.Contracts.Players;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

/// <summary>
/// Projects Terraria-owned player state into Vega snapshots and applies targeted player actions on the game thread.
/// </summary>
public sealed class TerrariaPlayerBoundary : IPlayerDirectory, IPlayerOperations, IPlayerMessenger, IDisposable
{
	private readonly IGameThreadDispatcher dispatcher;
	private IReadOnlyList<PlayerSnapshot> onlinePlayers = Array.Empty<PlayerSnapshot>();
	private bool attached;

	public TerrariaPlayerBoundary(IGameThreadDispatcher dispatcher)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		this.dispatcher = dispatcher;
	}

	public void Attach()
	{
		if (attached)
		{
			return;
		}

		HookEvents.Terraria.Main.Update += OnUpdate;
		attached = true;
	}

	public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Volatile.Read(ref onlinePlayers);

	public bool TryGet(PlayerId id, out PlayerSnapshot? player)
	{
		foreach (var candidate in SnapshotOnlinePlayers())
		{
			if (candidate.Id == id)
			{
				player = candidate;
				return true;
			}
		}

		player = null;
		return false;
	}

	public bool TryFindExact(string name, out PlayerSnapshot? player)
	{
		ArgumentNullException.ThrowIfNull(name);

		foreach (var candidate in SnapshotOnlinePlayers())
		{
			if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
			{
				player = candidate;
				return true;
			}
		}

		player = null;
		return false;
	}

	public ValueTask<PlayerOperationResult> KickAsync(
		PlayerId id,
		string reason,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(reason);

		return DispatchAsync(
			"player.kick",
			() =>
			{
				if (!TryGetActivePlayer(id, out _))
				{
					return PlayerOperationResult.NotFound();
				}

				global::Terraria.NetMessage.SendData(
					MessageID.Kick,
					id.Slot,
					-1,
					NetworkText.FromLiteral(reason));

				return PlayerOperationResult.Applied();
			},
			"player.kick_failed",
			cancellationToken);
	}

	public ValueTask<PlayerOperationResult> SendMessageAsync(
		PlayerId id,
		string message,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(message);

		return DispatchAsync(
			"player.message",
			() =>
			{
				if (!TryGetActivePlayer(id, out _))
				{
					return PlayerOperationResult.NotFound();
				}

				foreach (var line in message.ReplaceLineEndings("\n").Split('\n'))
				{
					ChatHelper.SendChatMessageToClient(
						NetworkText.FromLiteral(line),
						Color.White,
						id.Slot);
				}

				return PlayerOperationResult.Applied();
			},
			"player.message_failed",
			cancellationToken);
	}

	public ValueTask<PlayerOperationResult> TeleportAsync(
		PlayerId id,
		PlayerPosition destination,
		CancellationToken cancellationToken = default)
	{
		if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y))
		{
			return ValueTask.FromResult(PlayerOperationResult.Failed("player.teleport.invalid_destination"));
		}

		return DispatchAsync(
			"player.teleport",
			() =>
			{
				if (!TryGetActivePlayer(id, out var player))
				{
					return PlayerOperationResult.NotFound();
				}

				var x = Math.Clamp(
					destination.X,
					640f,
					global::Terraria.Main.rightWorld - 640f - player.width);
				var y = Math.Clamp(
					destination.Y,
					640f,
					global::Terraria.Main.bottomWorld - 640f - player.height);
				var position = new Vector2(x, y);
				var tileX = (short)(x / 16f - 7f);
				var tileY = (short)(y / 16f - 7f);

				global::Terraria.NetMessage.SendTileSquare(
					id.Slot,
					tileX,
					tileY,
					15,
					15,
					TileChangeType.None);
				global::Terraria.RemoteClient.CheckSection(id.Slot, position);
				player.Teleport(position, 1);
				global::Terraria.NetMessage.SendData(
					MessageID.TeleportEntity,
					-1,
					-1,
					NetworkText.Empty,
					0,
					player.whoAmI,
					x,
					y,
					1);

				return PlayerOperationResult.Applied();
			},
			"player.teleport_failed",
			cancellationToken);
	}

	public void Dispose()
	{
		if (attached)
		{
			HookEvents.Terraria.Main.Update -= OnUpdate;
			attached = false;
		}

		Volatile.Write(ref onlinePlayers, Array.Empty<PlayerSnapshot>());
	}

	private void OnUpdate(global::Terraria.Main instance, HookEvents.Terraria.Main.UpdateEventArgs args)
	{
		dispatcher.AssertGameThread();
		RefreshSnapshots();
	}

	private void RefreshSnapshots()
	{
		var players = global::Terraria.Main.player;
		var count = 0;

		for (var slot = 0; slot < players.Length; slot++)
		{
			if (players[slot] is { active: true })
			{
				count++;
			}
		}

		if (count == 0)
		{
			Volatile.Write(ref onlinePlayers, Array.Empty<PlayerSnapshot>());
			return;
		}

		var snapshots = new PlayerSnapshot[count];
		var index = 0;

		for (var slot = 0; slot < players.Length; slot++)
		{
			var player = players[slot];
			if (player is not { active: true })
			{
				continue;
			}

			snapshots[index++] = new PlayerSnapshot(
				new PlayerId(slot),
				player.name,
				player.dead,
				player.statLife,
				player.statLifeMax2,
				player.statMana,
				player.statManaMax2,
				new PlayerPosition(player.position.X, player.position.Y),
				player.hostile,
				player.team);
		}

		Volatile.Write(ref onlinePlayers, Array.AsReadOnly(snapshots));
	}

	private ValueTask<PlayerOperationResult> DispatchAsync(
		string name,
		Func<PlayerOperationResult> operation,
		string failureCode,
		CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return ValueTask.FromResult(PlayerOperationResult.Cancelled());
		}

		if (!dispatcher.IsGameThreadKnown)
		{
			return ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
		}

		if (dispatcher.IsGameThread)
		{
			return ValueTask.FromResult(ExecuteOperation(name, operation, failureCode));
		}

		var completion = new TaskCompletionSource<PlayerOperationResult>(
			TaskCreationOptions.RunContinuationsAsynchronously);

		if (!dispatcher.TryPost(
				name,
				() => completion.TrySetResult(
					cancellationToken.IsCancellationRequested
						? PlayerOperationResult.Cancelled()
						: ExecuteOperation(name, operation, failureCode))))
		{
			return ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());
		}

		return new ValueTask<PlayerOperationResult>(completion.Task);
	}

	private static PlayerOperationResult ExecuteOperation(
		string name,
		Func<PlayerOperationResult> operation,
		string failureCode)
	{
		try
		{
			return operation();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Vega player operation '{name}' failed: {ex}");
			return PlayerOperationResult.Failed(failureCode);
		}
	}

	private static bool TryGetActivePlayer(PlayerId id, out global::Terraria.Player player)
	{
		if ((uint)id.Slot >= (uint)global::Terraria.Main.maxPlayers)
		{
			player = null!;
			return false;
		}

		var candidate = global::Terraria.Main.player[id.Slot];
		if (candidate is not { active: true })
		{
			player = null!;
			return false;
		}

		player = candidate;
		return true;
	}
}
