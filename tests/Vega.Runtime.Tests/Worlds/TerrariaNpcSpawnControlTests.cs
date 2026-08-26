using NUnit.Framework;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class TerrariaNpcSpawnControlTests
{
    [Test]
    public async Task CaptureAndSetUseGameThreadAndRuntimeCapacity()
    {
        int originalMaximum = global::Terraria.NPC.defaultMaxSpawns;
        try
        {
            global::Terraria.NPC.defaultMaxSpawns = 5;
            var dispatcher = new InlineGameThreadDispatcher();
            var control = new TerrariaNpcSpawnControl(dispatcher, WorldId.Primary);

            NpcSpawnLimitSnapshot? before = await control.CaptureAsync(WorldId.Primary);
            NpcSpawnLimitMutationResult applied = await control.SetMaximumSpawnsAsync(
                WorldId.Primary,
                global::Terraria.Main.maxNPCs,
                "test");
            NpcSpawnLimitMutationResult invalid = await control.SetMaximumSpawnsAsync(
                WorldId.Primary,
                global::Terraria.Main.maxNPCs + 1,
                "test");

            Assert.Multiple(() =>
            {
                Assert.That(before?.MaximumSpawns, Is.EqualTo(5));
                Assert.That(before?.Capacity, Is.EqualTo(global::Terraria.Main.maxNPCs));
                Assert.That(applied.Status, Is.EqualTo(NpcOperationStatus.Applied));
                Assert.That(global::Terraria.NPC.defaultMaxSpawns, Is.EqualTo(global::Terraria.Main.maxNPCs));
                Assert.That(invalid.Status, Is.EqualTo(NpcOperationStatus.Invalid));
                Assert.That(dispatcher.AssertCalls, Is.EqualTo(4));
            });
        }
        finally
        {
            global::Terraria.NPC.defaultMaxSpawns = originalMaximum;
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
