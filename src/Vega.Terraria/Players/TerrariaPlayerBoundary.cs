using Microsoft.Xna.Framework;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Items;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;
using TerrariaRuntime;

namespace Vega.Terraria.Players;

/// <summary>
/// Projects Terraria-owned player state into Vega snapshots and applies targeted player actions on the game thread.
/// </summary>
public sealed partial class TerrariaPlayerBoundary :
	IPlayerDirectory,
	IPlayerCombatDirectory,
	IPlayerOperations,
	IPlayerNavigation,
	IPlayerItemDelivery,
	IPlayerItemSelection,
	IPlayerVitalOperations,
	IPlayerMessenger,
	IPlayerSessionLifecycle,
	IPlayerSessionClosing,
	IPlayerSessionEnteredWorld,
	IWorldInteractionState,
	IDisposable
{
	private readonly IGameThreadDispatcher dispatcher;
	private readonly TerrariaConnectionMetadataRegistry connectionMetadata;
	private readonly WorldId worldId;
	private IReadOnlyList<PlayerSnapshot> onlinePlayers = Array.Empty<PlayerSnapshot>();
	private PlayerCombatSnapshot?[] combatSnapshots = [];
	private PlayerSessionId?[] sessionIds = [];
	private global::Terraria.Net.Sockets.ISocket?[] sessionSockets = [];
	private string?[] sessionNames = [];
	private DateTimeOffset?[] sessionStartedAt = [];
	private string?[] sessionTransportEndpoints = [];
	private bool attached;

	public TerrariaPlayerBoundary(IGameThreadDispatcher dispatcher)
		: this(dispatcher, new TerrariaConnectionMetadataRegistry(), WorldId.Primary)
	{
	}

	public TerrariaPlayerBoundary(
		IGameThreadDispatcher dispatcher,
		TerrariaConnectionMetadataRegistry connectionMetadata,
		WorldId? worldId = null)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(connectionMetadata);
		this.dispatcher = dispatcher;
		this.connectionMetadata = connectionMetadata;
		this.worldId = worldId ?? WorldId.Primary;
	}

	public void Attach()
	{
		if (attached)
		{
			return;
		}

		HookEvents.Terraria.Main.Update += OnUpdate;
		global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnIdleUpdate;
		Hooks.Netplay.ServerConnectionClosing += OnConnectionClosing;
		Hooks.Netplay.ServerPlayerEnteringWorld += OnPlayerEnteringWorld;
		Hooks.Main.DedicatedWorldStopping += OnWorldStopping;
		attached = true;
	}

	public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Volatile.Read(ref onlinePlayers);

	public bool TryGetCombatSnapshot(PlayerId playerId, out PlayerCombatSnapshot? snapshot)
	{
		var current = Volatile.Read(ref combatSnapshots);
		snapshot = (uint)playerId.Slot < (uint)current.Length ? current[playerId.Slot] : null;
		return snapshot is not null;
	}

	public event Action<PlayerSessionOpened>? Opened;

	public event Action<PlayerSessionClosed>? Closed;
	public event Action<PlayerSessionClosed>? Closing;
	public event Action<PlayerSnapshot>? EnteredWorld;

	public bool TryGet(PlayerId id, out PlayerSnapshot? player)
	{
		var current = SnapshotOnlinePlayers();
		// RefreshSnapshots publishes players in ascending native slot order.
		int lo = 0, hi = current.Count - 1;
		while (lo <= hi)
		{
			int middle = lo + (hi - lo) / 2;
			var candidate = current[middle];
			if (candidate.Id == id)
			{
				player = candidate;
				return true;
			}
			if (candidate.Id.Slot < id.Slot) lo = middle + 1;
			else hi = middle - 1;
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
		CancellationToken cancellationToken = default) => KickCoreAsync(id,
		TryGet(id, out var snapshot) ? snapshot?.SessionId : null, reason, cancellationToken);

	public bool SupportsSessionBoundKicks => true;

	public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, PlayerSessionId sessionId, string reason,
		CancellationToken cancellationToken = default) => KickCoreAsync(id, sessionId, reason, cancellationToken);

	public ValueTask<PlayerOperationResult> KickAsync(PlayerSnapshot player, string reason,
		CancellationToken cancellationToken = default) => KickCoreAsync(player.Id,
		player.WorldId == worldId ? player.SessionId : null, reason, cancellationToken);

	private ValueTask<PlayerOperationResult> KickCoreAsync(PlayerId id, PlayerSessionId? expected, string reason,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(reason);

		return DispatchAsync(
			"player.kick",
			() =>
			{
				if (expected is not { } session || !TryGetSessionPlayer(id, session, out _))
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
		CancellationToken cancellationToken = default) => SendMessageCoreAsync(id, null, message, cancellationToken);

	public bool SupportsSessionBoundMessages => true;

	public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default) =>
		SendMessageCoreAsync(player.Id, player, message, cancellationToken);

	private ValueTask<PlayerOperationResult> SendMessageCoreAsync(PlayerId id, PlayerSnapshot? expected, string message, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(message);

		return DispatchAsync(
			"player.message",
			() =>
			{
				if (expected is null ? !TryGetActivePlayer(id, out _) : !TryGetSessionPlayer(expected, out _))
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
				return ApplyTeleport(player, destination);
			},
			"player.teleport_failed",
			cancellationToken);
	}

	public ValueTask<PlayerOperationResult> ToPositionAsync(PlayerSnapshot player, PlayerPosition destination, CancellationToken cancellationToken = default) =>
		NavigateAsync(player, live =>
		{
			if (!float.IsFinite(destination.X) || !float.IsFinite(destination.Y) || destination.X < 640f || destination.Y < 640f ||
				destination.X > global::Terraria.Main.rightWorld - 640f - live.width ||
				destination.Y > global::Terraria.Main.bottomWorld - 640f - live.height)
				return PlayerOperationResult.Failed("player.teleport.invalid_destination");
			return ApplyTeleport(live, destination);
		}, cancellationToken);

	public ValueTask<PlayerOperationResult> ToPlayerAsync(PlayerSnapshot player, PlayerSnapshot destination, CancellationToken cancellationToken = default) =>
		NavigateAsync(player, live =>
		{
			if (!TryGetSessionPlayer(destination, out var target) || target.dead)
				return PlayerOperationResult.NotFound();
			return ApplyTeleport(live, new PlayerPosition(target.Center.X - live.width / 2f, target.Bottom.Y - live.height));
		}, cancellationToken);

	public ValueTask<PlayerOperationResult> ToWorldSpawnAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
		NavigateAsync(player, live => ApplyTeleport(live, SpawnPosition(live, global::Terraria.Main.spawnTileX, global::Terraria.Main.spawnTileY)), cancellationToken);

	public ValueTask<PlayerOperationResult> ToHomeAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
		NavigateAsync(player, live =>
		{
			int x = live.SpawnX, y = live.SpawnY;
			if (x < 0 || y < 0 || x >= global::Terraria.Main.maxTilesX || y >= global::Terraria.Main.maxTilesY ||
				!global::Terraria.Player.CheckSpawn(x, y))
			{
				x = global::Terraria.Main.spawnTileX;
				y = global::Terraria.Main.spawnTileY;
			}
			return ApplyTeleport(live, SpawnPosition(live, x, y));
		}, cancellationToken);

	private static PlayerPosition SpawnPosition(global::Terraria.Player player, int x, int y) =>
		new(x * 16f + 8f - player.width / 2f, y * 16f - player.height);

	private ValueTask<PlayerOperationResult> NavigateAsync(PlayerSnapshot player, Func<global::Terraria.Player, PlayerOperationResult> navigate, CancellationToken cancellationToken) =>
		DispatchAsync("player.navigation", () => !TryGetSessionPlayer(player, out var live)
			? PlayerOperationResult.NotFound()
			: live.dead ? PlayerOperationResult.Failed("player.teleport.dead") : navigate(live),
			"player.teleport_failed", cancellationToken);

	internal bool TryGetSessionPlayer(PlayerSnapshot expected, out global::Terraria.Player player)
	{
		if (expected.WorldId == worldId && expected.SessionId is { } session && TryGetSessionPlayer(expected.Id, session, out player)) return true;
		player = null!;
		return false;
	}

	private bool TryGetSessionPlayer(PlayerId id, PlayerSessionId session, out global::Terraria.Player player)
	{
		int slot = id.Slot;
		if ((uint)slot < (uint)sessionIds.Length &&
			sessionIds[slot] == session && TryGetActivePlayer(id, out player) && sessionSockets[slot] is { } socket &&
			ReferenceEquals(socket, GetSocket(slot)))
			return true;
		player = null!;
		return false;
	}

	private static PlayerOperationResult ApplyTeleport(global::Terraria.Player player, PlayerPosition destination)
	{
		var x = Math.Clamp(destination.X, 640f, global::Terraria.Main.rightWorld - 640f - player.width);
		var y = Math.Clamp(destination.Y, 640f, global::Terraria.Main.bottomWorld - 640f - player.height);
		var position = new Vector2(x, y);
		var tileX = (short)(x / 16f - 7f);
		var tileY = (short)(y / 16f - 7f);

		global::Terraria.NetMessage.SendTileSquare(player.whoAmI, tileX, tileY, 15, 15, TileChangeType.None);
		global::Terraria.RemoteClient.CheckSection(player.whoAmI, position);
		player.Teleport(position, 1);
		global::Terraria.NetMessage.SendData(MessageID.TeleportEntity, -1, -1, NetworkText.Empty, 0, player.whoAmI, x, y, 1);

		return PlayerOperationResult.Applied();
	}

	public void Dispose()
	{
		if (attached)
		{
			HookEvents.Terraria.Main.Update -= OnUpdate;
			global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnIdleUpdate;
			Hooks.Netplay.ServerConnectionClosing -= OnConnectionClosing;
			Hooks.Netplay.ServerPlayerEnteringWorld -= OnPlayerEnteringWorld;
			Hooks.Main.DedicatedWorldStopping -= OnWorldStopping;
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
		Volatile.Write(ref combatSnapshots, Array.Empty<PlayerCombatSnapshot?>());
		sessionIds = [];
		sessionSockets = [];
		sessionNames = [];
		sessionStartedAt = [];
		sessionTransportEndpoints = [];
	}

	private void OnUpdate(global::Terraria.Main instance, HookEvents.Terraria.Main.UpdateEventArgs args)
	{
		RefreshOnGameThread();
	}

	private void OnConnectionClosing(int slot, global::Terraria.Net.Sockets.ISocket closingSocket)
	{
		if (!dispatcher.IsGameThread || (uint)slot >= (uint)sessionIds.Length || sessionIds[slot] is not { } session ||
			sessionSockets[slot] is not { } socket || !ReferenceEquals(closingSocket, socket)) return;
		ClearAdminGodMode(slot, session);
		try { Closing?.Invoke(new PlayerSessionClosed(session, new PlayerId(slot), sessionNames[slot] ?? string.Empty, DateTimeOffset.UtcNow)); }
		catch (Exception ex) { Console.Error.WriteLine($"Vega pre-reset session capture failed: {ex}"); }
	}

	private void OnPlayerEnteringWorld(int slot, global::Terraria.Net.Sockets.ISocket socket)
	{
		if (!dispatcher.IsGameThread || !ReferenceEquals(GetSocket(slot), socket)) return;
		RefreshOnGameThread();
		if (TryGet(new PlayerId(slot), out var player) && player is { SessionId: not null } && TryGetSessionPlayer(player, out _))
		{
			ClearStaleAdminGodMode(slot, player.SessionId.Value);
			try { EnteredWorld?.Invoke(player); }
			catch (Exception ex) { Console.Error.WriteLine($"Vega world-entry snapshot failed: {ex}"); }
		}
	}

	private void OnWorldStopping()
	{
		if (!dispatcher.IsGameThread) return;
		for (int slot = 0; slot < sessionIds.Length; slot++)
		{
			if (sessionIds[slot] is not { } session || sessionSockets[slot] is not { } socket ||
				!ReferenceEquals(GetSocket(slot), socket)) continue;
			ClearAdminGodMode(slot, session);
			try { Closing?.Invoke(new PlayerSessionClosed(session, new PlayerId(slot), sessionNames[slot] ?? string.Empty, DateTimeOffset.UtcNow)); }
			catch (Exception ex) { Console.Error.WriteLine($"Vega shutdown session capture failed: {ex}"); }
		}
	}

	public bool TryGetActiveChest(PlayerId playerId, out int chestId)
	{
		global::Terraria.Player[] players = global::Terraria.Main.player;
		if ((uint)playerId.Slot >= (uint)players.Length || players[playerId.Slot] is not { active: true } player)
		{
			chestId = -1;
			return false;
		}

		chestId = player.chest;
		return chestId >= 0;
	}

	public bool TryGetChestPosition(int chestId, out WorldTilePosition position)
	{
		global::Terraria.Chest?[] chests = global::Terraria.Main.chest;
		if ((uint)chestId >= (uint)chests.Length || chests[chestId] is not { } chest)
		{
			position = default;
			return false;
		}

		position = new WorldTilePosition(chest.x, chest.y);
		return true;
	}

	public bool TryGetTileEntityPosition(int tileEntityId, out WorldTilePosition position)
	{
		if (!global::Terraria.DataStructures.TileEntity.ByID.TryGetValue(
				tileEntityId,
				out global::Terraria.DataStructures.TileEntity? entity))
		{
			position = default;
			return false;
		}

		position = new WorldTilePosition(entity.Position.X, entity.Position.Y);
		return true;
	}

	private void OnIdleUpdate()
	{
		if (!global::Terraria.Netplay.HasFullyConnectedClients)
		{
			RefreshOnGameThread();
		}
	}

	private void RefreshOnGameThread()
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
			Volatile.Write(ref combatSnapshots, Array.Empty<PlayerCombatSnapshot?>());
			return;
		}

		var snapshots = new PlayerSnapshot[count];
		var previousCombat = Volatile.Read(ref combatSnapshots);
		PlayerCombatSnapshot?[]? nextCombat = previousCombat.Length == players.Length ? null : new PlayerCombatSnapshot?[players.Length];
		var index = 0;

		for (var slot = 0; slot < players.Length; slot++)
		{
			var player = players[slot];
			if (player is not { active: true })
			{
				UpdateCombatSnapshot(previousCombat, ref nextCombat, slot, null);
				continue;
			}

			var playerId = new PlayerId(slot);
			string? remoteEndpoint = sessionIds[slot] is not null
				? sessionTransportEndpoints[slot] ??= TryGetRemoteEndpoint(slot)
				: TryGetRemoteEndpoint(slot);
			var connection = connectionMetadata.Snapshot(
				slot,
				remoteEndpoint,
				GetConnectionState(slot));
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
				WorldId = worldId,
				SessionId = sessionIds[slot],
				ConnectedAt = sessionStartedAt[slot],
				RemoteEndpoint = connection.EffectiveEndpoint,
				Platform = connection.Platform,
				Connection = connection,
				DeathsPve = player.numberOfDeathsPVE,
				DeathsPvp = player.numberOfDeathsPVP
			};

			global::Terraria.Item heldItem = player.HeldItem;
			UpdateCombatSnapshot(previousCombat, ref nextCombat, slot, checked((short)heldItem.type),
				heldItem.prefix, heldItem.useAmmo == AmmoID.Arrow);
		}

		Volatile.Write(ref onlinePlayers, Array.AsReadOnly(snapshots));
		if (nextCombat is not null)
			Volatile.Write(ref combatSnapshots, nextCombat);
	}

	// Readers may retain snapshots from earlier ticks. Clone the slot array only on the
	// first changed item/active state; unchanged entries remain immutable and reusable.
	internal static void UpdateCombatSnapshot(PlayerCombatSnapshot?[] previous, ref PlayerCombatSnapshot?[]? next,
		int slot, short? heldItemType, byte prefix = 0, bool arrowAmmo = false)
	{
		var current = (next ?? previous)[slot];
		if (heldItemType is null ? current is null :
			current is not null && current.HeldItemType == heldItemType.Value &&
			current.HeldItemPrefix == prefix && current.UsesArrowAmmo == arrowAmmo)
			return;
		next ??= (PlayerCombatSnapshot?[])previous.Clone();
		next[slot] = heldItemType is short type ? new PlayerCombatSnapshot(new PlayerId(slot), type, prefix, arrowAmmo) : null;
	}

	private void RefreshSessions(global::Terraria.Player[] players)
	{
		if (sessionIds.Length != players.Length)
		{
			sessionIds = new PlayerSessionId?[players.Length];
			sessionSockets = new global::Terraria.Net.Sockets.ISocket?[players.Length];
			sessionNames = new string?[players.Length];
			sessionStartedAt = new DateTimeOffset?[players.Length];
			sessionTransportEndpoints = new string?[players.Length];
		}

		var timestamp = DateTimeOffset.UtcNow;
		for (var slot = 0; slot < players.Length; slot++)
		{
			global::Terraria.Player? player = players[slot];
			var socket = GetSocket(slot);
			if (sessionIds[slot] is PlayerSessionId previous &&
				(player is not { active: true } || !ReferenceEquals(sessionSockets[slot], socket)))
			{
				Closed?.Invoke(new PlayerSessionClosed(previous, new PlayerId(slot), sessionNames[slot] ?? string.Empty, timestamp));
				sessionIds[slot] = null;
				sessionSockets[slot] = null;
				sessionNames[slot] = null;
				sessionStartedAt[slot] = null;
				sessionTransportEndpoints[slot] = null;
			}
			if (player is { active: true } && socket is not null && sessionIds[slot] is null)
			{
				PlayerSessionId sessionId = PlayerSessionId.New();
				sessionIds[slot] = sessionId;
				sessionSockets[slot] = socket;
				sessionNames[slot] = player.name;
				sessionStartedAt[slot] = timestamp;
				sessionTransportEndpoints[slot] = null;
				Opened?.Invoke(new PlayerSessionOpened(
					sessionId,
					new PlayerId(slot),
					player.name,
					timestamp));
			}
		}
	}

	private static global::Terraria.Net.Sockets.ISocket? GetSocket(int slot) =>
		(uint)slot < (uint)global::Terraria.Netplay.Clients.Length ? global::Terraria.Netplay.Clients[slot]?.Socket : null;

	private static TerrariaConnectionState GetConnectionState(int slot)
	{
		int state = global::Terraria.Netplay.Clients[slot]?.State ??
			(int)TerrariaConnectionState.PasswordChallenge;
		return (TerrariaConnectionState)state;
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
