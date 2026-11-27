using NUnit.Framework;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TerrariaPlayerBoundaryTests
{
	[Test]
	[NonParallelizable]
	public void NativeChestCapacityReadsExactRegisteredStorageOnOwningThread()
	{
		var oldChests = global::Terraria.Main.chest;
		var oldCoordinates = global::Terraria.Chest._chestsByCoords;
		var dispatcher = new FakeGameThreadDispatcher(isGameThreadKnown: true);
		using var boundary = new TerrariaPlayerBoundary(dispatcher);
		try
		{
			global::Terraria.Main.chest = new global::Terraria.Chest[oldChests.Length];
			global::Terraria.Chest._chestsByCoords = [];
			var chest = global::Terraria.Chest.CreateWorldChest(0, 10, 10);
			Assert.That(boundary.TryGetChestCapacity(0, out _), Is.False);
			dispatcher.TryPost("test native chest capacity", () =>
			{
				foreach (int size in new[] { 40, 80, 200 })
				{
					chest.Resize(size);
					Assert.That(boundary.TryGetChestCapacity(0, out var capacity), Is.True);
					Assert.That(capacity, Is.EqualTo(size));
				}
				Assert.That(boundary.TryGetChestCapacity(1, out _), Is.False);
				global::Terraria.Chest.RemoveChest(0);
				Assert.That(boundary.TryGetChestCapacity(0, out _), Is.False);
			});
			dispatcher.RunPending();
		}
		finally
		{
			global::Terraria.Main.chest = oldChests;
			global::Terraria.Chest._chestsByCoords = oldCoordinates;
		}
	}

	[Test]
	public void Combat_read_model_reuses_unchanged_slots_and_does_not_mutate_published_tick()
	{
		PlayerCombatSnapshot?[] previous = [new(new PlayerId(0), 10, 1, false), null, new(new PlayerId(2), 20, 0, true)];
		PlayerCombatSnapshot?[]? next = null;
		TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, 0, 10, 1, false);
		TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, 1, null);
		TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, 2, 20, 0, true);
		Assert.That(next, Is.Null);

		TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, 0, 10, 2, true);
		TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, 1, 15);
		TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, 2, null);
		Assert.That(next, Is.Not.SameAs(previous));
		Assert.That(next![0], Is.EqualTo(new PlayerCombatSnapshot(new PlayerId(0), 10, 2, true)));
		Assert.That(next[1], Is.EqualTo(new PlayerCombatSnapshot(new PlayerId(1), 15, 0, false)));
		Assert.That(next[2], Is.Null);
		Assert.That(previous[0]!.HeldItemPrefix, Is.EqualTo(1));
		Assert.That(previous[1], Is.Null);
		Assert.That(previous[2]!.HeldItemType, Is.EqualTo(20));

		PlayerCombatSnapshot?[]? initialized = new PlayerCombatSnapshot?[2];
		TerrariaPlayerBoundary.UpdateCombatSnapshot([], ref initialized, 1, 0);
		Assert.That(initialized![1]!.HeldItemType, Is.Zero);
	}

	[Test]
	public void Stable_combat_read_model_does_not_allocate_per_tick()
	{
		const int players = 200;
		const int ticks = 1000;
		PlayerCombatSnapshot?[] previous = new PlayerCombatSnapshot?[256];
		for (int i = 0; i < players; i++) previous[i] = new(new PlayerId(i), 10, 0, true);
		void Refresh()
		{
			PlayerCombatSnapshot?[]? next = null;
			for (int i = 0; i < previous.Length; i++)
				TerrariaPlayerBoundary.UpdateCombatSnapshot(previous, ref next, i, i < players ? (short)10 : null, 0, true);
			if (next is not null) throw new InvalidOperationException("Stable state changed.");
		}
		for (int i = 0; i < 100; i++) Refresh();
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < ticks; i++) Refresh();
		long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
		// Previous implementation rebuilt the dictionary and all combat records every tick.
		before = GC.GetAllocatedBytesForCurrentThread();
		for (int tick = 0; tick < ticks; tick++)
		{
			var baseline = new Dictionary<PlayerId, PlayerCombatSnapshot>(players);
			for (int i = 0; i < players; i++) baseline[new PlayerId(i)] = new(new PlayerId(i), 10, 0, true);
			GC.KeepAlive(baseline);
		}
		long baselineAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
		TestContext.Out.WriteLine($"{players} stable combat slots over {ticks} ticks: {baselineAllocated} -> {allocated} bytes.");
		Assert.That(allocated, Is.Zero);
	}

	[Test]
	public async Task Queued_operation_completes_with_execution_result()
	{
		var dispatcher = new FakeGameThreadDispatcher(isGameThreadKnown: true);
		using var boundary = new TerrariaPlayerBoundary(dispatcher);

		var pending = boundary.KickAsync(new PlayerId(-1), "test");

		Assert.That(pending.IsCompleted, Is.False);
		dispatcher.RunPending();

		var result = await pending;
		Assert.That(result.Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));
	}

	[Test]
	public async Task Cancellation_before_game_thread_execution_prevents_operation()
	{
		var dispatcher = new FakeGameThreadDispatcher(isGameThreadKnown: true);
		using var boundary = new TerrariaPlayerBoundary(dispatcher);
		using var cancellation = new CancellationTokenSource();

		var pending = boundary.KickAsync(new PlayerId(-1), "test", cancellation.Token);
		cancellation.Cancel();
		dispatcher.RunPending();

		var result = await pending;
		Assert.That(result.Status, Is.EqualTo(PlayerOperationStatus.Cancelled));
	}

	[Test]
	public async Task Unknown_game_thread_returns_dispatcher_unavailable()
	{
		var dispatcher = new FakeGameThreadDispatcher(isGameThreadKnown: false);
		using var boundary = new TerrariaPlayerBoundary(dispatcher);

		var result = await boundary.SendMessageAsync(new PlayerId(-1), "test");

		Assert.That(result.Status, Is.EqualTo(PlayerOperationStatus.DispatcherUnavailable));
	}

	[Test]
	public async Task Full_dispatch_queue_returns_dispatcher_unavailable()
	{
		var dispatcher = new FakeGameThreadDispatcher(isGameThreadKnown: true, acceptPosts: false);
		using var boundary = new TerrariaPlayerBoundary(dispatcher);

		var result = await boundary.KickAsync(new PlayerId(-1), "test");

		Assert.That(result.Status, Is.EqualTo(PlayerOperationStatus.DispatcherUnavailable));
	}

	[Test]
	public async Task Snapshot_kick_does_not_fall_back_to_an_unbound_slot()
	{
		var dispatcher = new FakeGameThreadDispatcher(isGameThreadKnown: true);
		using var boundary = new TerrariaPlayerBoundary(dispatcher);
		var stale = new PlayerSnapshot(new PlayerId(0), "Old connection", false, 100, 100, 20, 20, new(0, 0), false, 0)
			{ WorldId = new WorldId("other-world"), SessionId = PlayerSessionId.New() };
		var pending = boundary.KickAsync(stale, "test");
		dispatcher.RunPending();
		Assert.That((await pending).Status, Is.EqualTo(PlayerOperationStatus.PlayerNotFound));
	}

	private sealed class FakeGameThreadDispatcher(bool isGameThreadKnown, bool acceptPosts = true)
		: IGameThreadDispatcher
	{
		private readonly Queue<Action> pending = new();

		public bool IsGameThreadKnown { get; } = isGameThreadKnown;

		public bool IsGameThread { get; private set; }

		public void AssertGameThread()
		{
			if (!IsGameThread)
			{
				throw new InvalidOperationException("Not on the fake game thread.");
			}
		}

		public bool TryPost(string name, Action action)
		{
			if (!acceptPosts)
			{
				return false;
			}

			pending.Enqueue(action);
			return true;
		}

		public bool TrySchedule(string name, Func<bool> step) =>
			throw new NotSupportedException();

		public void RunPending()
		{
			IsGameThread = true;
			try
			{
				while (pending.TryDequeue(out var action))
				{
					action();
				}
			}
			finally
			{
				IsGameThread = false;
			}
		}
	}
}
