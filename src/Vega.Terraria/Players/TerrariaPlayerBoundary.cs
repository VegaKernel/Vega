using Microsoft.Xna.Framework;
using Terraria.Chat;
using Terraria.ID;
using Terraria.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Players;

/// <summary>
/// Projects Terraria-owned player state into Vega snapshots and applies targeted player actions on the game thread.
/// </summary>
public sealed class TerrariaPlayerBoundary :
	IPlayerDirectory,
	IPlayerCombatDirectory,
	IPlayerLifecycleOperations,
	IPlayerMessenger,
	IPlayerSessionLifecycle,
	IWorldInteractionState,
	IDisposable
{
	private readonly IGameThreadDispatcher dispatcher;
	private readonly TerrariaConnectionMetadataRegistry connectionMetadata;
	private readonly WorldId worldId;
	private IReadOnlyList<PlayerSnapshot> onlinePlayers = Array.Empty<PlayerSnapshot>();
	private IReadOnlyDictionary<PlayerId, PlayerCombatSnapshot> combatSnapshots =
		new Dictionary<PlayerId, PlayerCombatSnapshot>();
	private PlayerSessionId?[] sessionIds = [];
	private string?[] sessionNames = [];
	private DateTimeOffset?[] sessionStartedAt = [];
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

	public ValueTask<PlayerOperationResult> HealAsync(
		PlayerId id,
		int? amount = null,
		CancellationToken cancellationToken = default)
	{
		if (amount is <= 0)
		{
			return ValueTask.FromResult(PlayerOperationResult.Failed("player.heal.invalid_amount"));
		}

		return DispatchAsync(
			"player.heal",
			() =>
			{
				if (!TryGetActivePlayer(id, out global::Terraria.Player player))
				{
					return PlayerOperationResult.NotFound();
				}

				if (player.dead)
				{
					return PlayerOperationResult.Failed("player.heal.dead");
				}

				int maximum = Math.Max(1, player.statLifeMax2);
				int missing = Math.Max(0, maximum - player.statLife);
				int applied = Math.Min(missing, amount ?? missing);
				if (applied > 0)
				{
					player.Heal(applied);
					global::Terraria.NetMessage.SendData(
						MessageID.PlayerLifeMana,
						-1,
						-1,
						null,
						id.Slot);
				}

				return PlayerOperationResult.Applied();
			},
			"player.heal_failed",
			cancellationToken);
	}

	public ValueTask<PlayerOperationResult> KillAsync(
		PlayerId id,
		string reason,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);

		return DispatchAsync(
			"player.kill",
			() =>
			{
				if (!TryGetActivePlayer(id, out global::Terraria.Player player))
				{
					return PlayerOperationResult.NotFound();
				}

				if (player.dead)
				{
					return PlayerOperationResult.Failed("player.kill.already_dead");
				}

				var deathReason = global::Terraria.DataStructures.PlayerDeathReason.ByCustomReason(reason);
				double damage = Math.Max(1d, Math.Max(player.statLife, player.statLifeMax2) + 1d);
				player.KillMe(deathReason, damage, 0, pvp: false);
				return PlayerOperationResult.Applied();
			},
			"player.kill_failed",
			cancellationToken);
	}

	public ValueTask<PlayerOperationResult> ApplyBuffAsync(
		PlayerId id,
		int buffType,
		int durationTicks,
		CancellationToken cancellationToken = default)
	{
		if (buffType <= 0 || buffType >= BuffID.Count)
		{
			return ValueTask.FromResult(PlayerOperationResult.Failed("player.buff.invalid_type"));
		}

		if (durationTicks <= 0)
		{
			return ValueTask.FromResult(PlayerOperationResult.Failed("player.buff.invalid_duration"));
		}

		return DispatchAsync(
			"player.buff",
			() =>
			{
				if (!TryGetActivePlayer(id, out global::Terraria.Player player))
				{
					return PlayerOperationResult.NotFound();
				}

				if (player.dead)
				{
					return PlayerOperationResult.Failed("player.buff.dead");
				}

				player.AddBuff(buffType, durationTicks, false);
				global::Terraria.NetMessage.SendData(
					MessageID.PlayerBuffs,
					-1,
					-1,
					null,
					id.Slot);
				return PlayerOperationResult.Applied();
			},
			"player.buff_failed",
			cancellationToken);
	}

	public ValueTask<PlayerOperationResult> RespawnAsync(
		PlayerId id,
		CancellationToken cancellationToken = default) =>
		DispatchAsync(
			"player.respawn",
			() =>
			{
				if (!TryGetActivePlayer(id, out global::Terraria.Player player))
				{
					return PlayerOperationResult.NotFound();
				}

				if (!player.dead)
				{
					return PlayerOperationResult.Failed("player.respawn.not_dead");
				}

				player.respawnTimer = 0;
				player.Spawn(global::Terraria.PlayerSpawnContext.ReviveFromDeath);
				global::Terraria.NetMessage.SendData(
					MessageID.PlayerSpawn,
					-1,
					-1,
					null,
					id.Slot,
					(float)global::Terraria.PlayerSpawnContext.ReviveFromDeath);
				return PlayerOperationResult.Applied();
			},
			"player.respawn_failed",
			cancellationToken);

	public void Dispose()
	{
		if (attached)
		{
			HookEvents.Terraria.Main.Update -= OnUpdate;
			global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnIdleUpdate;
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
		RefreshOnGameThread();
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
			string? remoteEndpoint = TryGetRemoteEndpoint(slot);
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
				Connection = connection
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
				connectionMetadata.Remove(slot);
			}
		}
	}

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
