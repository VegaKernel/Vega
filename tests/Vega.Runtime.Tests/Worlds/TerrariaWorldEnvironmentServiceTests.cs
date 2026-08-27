using NUnit.Framework;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class TerrariaWorldEnvironmentServiceTests
{
    [Test]
    public async Task GameModePatchMutatesTerrariaStateAndCapturesMode()
    {
        int originalGameMode = global::Terraria.Main.GameMode;
        int originalNetMode = global::Terraria.Main.netMode;
        try
        {
            global::Terraria.Main.GameMode = 0;
            global::Terraria.Main.netMode = 0;
            var dispatcher = new InlineGameThreadDispatcher();
            var service = new TerrariaWorldEnvironmentService(dispatcher, WorldId.Primary);

            WorldEnvironmentMutationResult applied = await service.PatchAsync(
                new WorldEnvironmentMutationRequest(
                    WorldId.Primary,
                    new WorldEnvironmentPatch(GameMode: 2),
                    "test"));
            WorldEnvironmentMutationResult invalid = await service.PatchAsync(
                new WorldEnvironmentMutationRequest(
                    WorldId.Primary,
                    new WorldEnvironmentPatch(GameMode: 4),
                    "test"));

            Assert.Multiple(() =>
            {
                Assert.That(applied.Status, Is.EqualTo(WorldEnvironmentMutationStatus.Applied));
                Assert.That(applied.Snapshot?.GameMode, Is.EqualTo(2));
                Assert.That(global::Terraria.Main.GameMode, Is.EqualTo(2));
                Assert.That(invalid.Status, Is.EqualTo(WorldEnvironmentMutationStatus.Invalid));
                Assert.That(invalid.ErrorCode, Is.EqualTo("world.environment.invalid_game_mode"));
                Assert.That(global::Terraria.Main.GameMode, Is.EqualTo(2));
                Assert.That(dispatcher.AssertCalls, Is.EqualTo(3));
            });
        }
        finally
        {
            global::Terraria.Main.GameMode = originalGameMode;
            global::Terraria.Main.netMode = originalNetMode;
        }
    }

    private sealed class InlineGameThreadDispatcher : IGameThreadDispatcher
    {
        public int AssertCalls { get; private set; }
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() => AssertCalls++;
        public bool TryPost(string name, Action action)
        {
            action();
            return true;
        }

        public bool TrySchedule(string name, Func<bool> step)
        {
            step();
            return true;
        }
    }
}
