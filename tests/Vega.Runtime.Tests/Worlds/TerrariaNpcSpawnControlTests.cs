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
        int originalSpawnRate = global::Terraria.NPC.defaultSpawnRate;
        try
        {
            global::Terraria.NPC.defaultMaxSpawns = 5;
            global::Terraria.NPC.defaultSpawnRate = 600;
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
            NpcSpawnRateSnapshot? rateBefore = await control.CaptureSpawnRateAsync(WorldId.Primary);
            NpcSpawnRateMutationResult rateApplied = await control.SetSpawnRateAsync(
                WorldId.Primary,
                0,
                "test");
            NpcSpawnRateMutationResult rateInvalid = await control.SetSpawnRateAsync(
                WorldId.Primary,
                -1,
                "test");

            Assert.Multiple(() =>
            {
                Assert.That(before?.MaximumSpawns, Is.EqualTo(5));
                Assert.That(before?.Capacity, Is.EqualTo(global::Terraria.Main.maxNPCs));
                Assert.That(applied.Status, Is.EqualTo(NpcOperationStatus.Applied));
                Assert.That(global::Terraria.NPC.defaultMaxSpawns, Is.EqualTo(global::Terraria.Main.maxNPCs));
                Assert.That(invalid.Status, Is.EqualTo(NpcOperationStatus.Invalid));
                Assert.That(rateBefore?.SpawnRate, Is.EqualTo(600));
                Assert.That(rateApplied.Status, Is.EqualTo(NpcOperationStatus.Applied));
                Assert.That(global::Terraria.NPC.defaultSpawnRate, Is.EqualTo(0));
                Assert.That(rateInvalid.Status, Is.EqualTo(NpcOperationStatus.Invalid));
                Assert.That(dispatcher.AssertCalls, Is.EqualTo(8));
            });
        }
        finally
        {
            global::Terraria.NPC.defaultMaxSpawns = originalMaximum;
            global::Terraria.NPC.defaultSpawnRate = originalSpawnRate;
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
