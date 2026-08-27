using NUnit.Framework;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class TerrariaAnglerQuestServiceTests
{
    [Test]
    public async Task TargetedAndGlobalClearMutateDailyListOnGameThread()
    {
        List<string> completed = global::Terraria.Main.anglerWhoFinishedToday;
        string[] original = completed.ToArray();
        int originalNetMode = global::Terraria.Main.netMode;
        try
        {
            global::Terraria.Main.netMode = 0;
            completed.Clear();
            completed.AddRange(["Alice", "ALICE", "Bob"]);
            var dispatcher = new InlineGameThreadDispatcher();
            var service = new TerrariaAnglerQuestService(dispatcher, WorldId.Primary);

            AnglerQuestClearResult targeted = await service.ClearAsync(
                new AnglerQuestClearRequest(WorldId.Primary, "alice", "test"));
            AnglerQuestClearResult missing = await service.ClearAsync(
                new AnglerQuestClearRequest(WorldId.Primary, "missing", "test"));
            AnglerQuestClearResult all = await service.ClearAsync(
                new AnglerQuestClearRequest(WorldId.Primary, null, "test"));

            Assert.Multiple(() =>
            {
                Assert.That(targeted.Status, Is.EqualTo(AnglerQuestClearStatus.Applied));
                Assert.That(targeted.RemovedEntries, Is.EqualTo(2));
                Assert.That(missing.Status, Is.EqualTo(AnglerQuestClearStatus.NotFound));
                Assert.That(all.Status, Is.EqualTo(AnglerQuestClearStatus.Applied));
                Assert.That(all.RemovedEntries, Is.EqualTo(1));
                Assert.That(completed, Is.Empty);
                Assert.That(dispatcher.AssertCalls, Is.EqualTo(3));
            });
        }
        finally
        {
            completed.Clear();
            completed.AddRange(original);
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
