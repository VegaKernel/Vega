using NUnit.Framework;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Terraria.Players;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class TerrariaPlayerBoundaryTests
{
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
