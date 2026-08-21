using Microsoft.Xna.Framework;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

/// <summary>
/// Projects Terraria-owned player state into Vega snapshots and applies targeted player actions on the game thread.
/// </summary>
public sealed class TerrariaPlayerBoundary :
	IPlayerDirectory,
	IPlayerCombatDirectory,
	IPlayerOperations,
	IPlayerMessenger,
	IPlayerSessionLifecycle,
	IDisposable
{
	private readonly IGameThreadDispatcher dispatcher;
	private IReadOnlyList<PlayerSnapshot> onlinePlayers = Array.Empty<PlayerSnapshot>();
	private IReadOnlyDictionary<PlayerId, PlayerCombatSnapshot> combatSnapshots =
		new Dictionary<PlayerId, PlayerCombatSnapshot>();
	private PlayerSessionId?[] sessionIds = [];
	private string?[] sessionNames = [];
	private DateTimeOffset?[] sessionStartedAt = [];
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

	public bool TryGetCombatSnapshot(PlayerId playerId, out PlayerCombatSnapshot? snapshot) =>
		Volatile.Read(ref combatSnapshots).TryGetValue(playerId, out snapshot);

	public event Action<PlayerSessionOpened>? Opened;

	public event Action<PlayerSessionClosed>? Closed;

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

		var timestamp = DateTimeOffset.UtcNow;
		for (var slot = 0; slot < sessionIds.Length; slot++)
		{
			if (sessionIds[slot] is PlayerSessionId sessionId)
			{
				Closed?.Invoke(new PlayerSessionClosed(
					sessionId,
					new PlayerId(slot),
					sessionNames[slot] ?? string.Empty,
					timestamp));
			}
		}

		Volatile.Write(ref onlinePlayers, Array.Empty<PlayerSnapshot>());
		Volatile.Write(
			ref combatSnapshots,
			new Dictionary<PlayerId, PlayerCombatSnapshot>());
		sessionIds = [];
		sessionNames = [];
		sessionStartedAt = [];
	}

	private void OnUpdate(global::Terraria.Main instance, HookEvents.Terraria.Main.UpdateEventArgs args)
	{
		try
		{
			dispatcher.AssertGameThread();
			RefreshSnapshots();
		}
		catch (Exception ex)
		{
			// Player read-model projection is observability/supporting infrastructure.
			// A bad snapshot must never tear down the Terraria update loop.
			Console.Error.WriteLine($"Vega player snapshot refresh failed: {ex}");
		}
	}

	private void RefreshSnapshots()
	{
		var players = global::Terraria.Main.player;
		RefreshSessions(players);
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
			Volatile.Write(
				ref combatSnapshots,
				new Dictionary<PlayerId, PlayerCombatSnapshot>());
			return;
		}

		var snapshots = new PlayerSnapshot[count];
		var combat = new Dictionary<PlayerId, PlayerCombatSnapshot>(count);
		var index = 0;

		for (var slot = 0; slot < players.Length; slot++)
		{
			var player = players[slot];
			if (player is not { active: true })
			{
				continue;
			}

			var playerId = new PlayerId(slot);
			snapshots[index++] = new PlayerSnapshot(
				playerId,
				player.name,
				player.dead,
				player.statLife,
				player.statLifeMax2,
				player.statMana,
				player.statManaMax2,
				new PlayerPosition(player.position.X, player.position.Y),
				player.hostile,
				player.team)
			{
				SessionId = sessionIds[slot],
				ConnectedAt = sessionStartedAt[slot],
				RemoteEndpoint = TryGetRemoteEndpoint(slot),
				Platform = null
			};

			global::Terraria.Item heldItem = player.HeldItem;
			combat[playerId] = new PlayerCombatSnapshot(
				playerId,
				checked((short)heldItem.type),
				heldItem.prefix,
				heldItem.useAmmo == AmmoID.Arrow);
		}

		Volatile.Write(ref onlinePlayers, Array.AsReadOnly(snapshots));
		Volatile.Write(ref combatSnapshots, combat);
	}

	private void RefreshSessions(global::Terraria.Player[] players)
	{
		if (sessionIds.Length != players.Length)
		{
			sessionIds = new PlayerSessionId?[players.Length];
			sessionNames = new string?[players.Length];
			sessionStartedAt = new DateTimeOffset?[players.Length];
		}

		var timestamp = DateTimeOffset.UtcNow;
		for (var slot = 0; slot < players.Length; slot++)
		{
			global::Terraria.Player? player = players[slot];
			if (player is { active: true } && sessionIds[slot] is null)
			{
				PlayerSessionId sessionId = PlayerSessionId.New();
				sessionIds[slot] = sessionId;
				sessionNames[slot] = player.name;
				sessionStartedAt[slot] = timestamp;
				Opened?.Invoke(new PlayerSessionOpened(
					sessionId,
					new PlayerId(slot),
					player.name,
					timestamp));
			}
			else if (player is not { active: true } && sessionIds[slot] is PlayerSessionId sessionId)
			{
				Closed?.Invoke(new PlayerSessionClosed(
					sessionId,
					new PlayerId(slot),
					sessionNames[slot] ?? string.Empty,
					timestamp));
				sessionIds[slot] = null;
				sessionNames[slot] = null;
				sessionStartedAt[slot] = null;
			}
		}
	}

	private static string? TryGetRemoteEndpoint(int slot)
	{
		try
		{
			return global::Terraria.Netplay.Clients[slot]?.Socket?
				.GetRemoteAddress()
				.GetIdentifier();
		}
		catch (Exception)
		{
			return null;
		}
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
