using System.Diagnostics;
using NUnit.Framework;
using Vega.Contracts.Threading;
using Vega.Terraria.Players;
using static Vega.Terraria.Players.TerrariaSandboxSscCharacterAdapter;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class TerrariaSscSnapshotTests
{
    [Test]
    public async Task WorkerCaptureEncodesOnlyTheDetachedSnapshotAndPreservesPayload()
    {
        var dispatcher = new ManualDispatcher();
        var adapter = new TerrariaSandboxSscCharacterAdapter(dispatcher);
        CharacterState snapshot = CreateCharacter();
        string expected = SerializeImportedCharacter(snapshot);

        var pending = adapter.CaptureDetachedAsync("test.capture", () =>
        {
            dispatcher.AssertGameThread();
            return new CaptureSnapshotResult(snapshot);
        }, CancellationToken.None);

        Assert.That(pending.IsCompleted, Is.False);
        dispatcher.RunPending();
        var captured = await pending;
        Assert.That(captured.Succeeded, Is.True);
        Assert.That(captured.Payload, Is.EqualTo(expected));
    }

    [Test]
    public void GameThreadCaptureCompletesSynchronouslyForPreResetLifecycle()
    {
        var dispatcher = new ManualDispatcher();
        var adapter = new TerrariaSandboxSscCharacterAdapter(dispatcher);
        CharacterState snapshot = CreateCharacter();
        dispatcher.Run(() =>
        {
            var pending = adapter.CaptureDetachedAsync("test.pre-reset", () => new(snapshot), CancellationToken.None);
            Assert.That(pending.IsCompletedSuccessfully, Is.True);
            Assert.That(pending.Result.Payload, Is.EqualTo(SerializeImportedCharacter(snapshot)));
        });
    }

    [Test]
    public void CancelledQueuedCaptureNeverReadsNativeState()
    {
        var dispatcher = new ManualDispatcher();
        var adapter = new TerrariaSandboxSscCharacterAdapter(dispatcher);
        using var cancellation = new CancellationTokenSource();
        int reads = 0;
        var pending = adapter.CaptureDetachedAsync("test.cancel", () =>
        {
            reads++;
            return new(CreateCharacter());
        }, cancellation.Token);
        cancellation.Cancel();
        dispatcher.RunPending();
        Assert.CatchAsync<OperationCanceledException>(async () => { await pending; });
        Assert.That(reads, Is.Zero);
    }

    [Test]
    public async Task FullCharacterCheckpointEncodingIsRemovedFromTheGameThread()
    {
        const int players = 255;
        var dispatcher = new ManualDispatcher();
        var adapter = new TerrariaSandboxSscCharacterAdapter(dispatcher);
        CharacterState snapshot = CreateCharacter();
        string expected = SerializeImportedCharacter(snapshot);
        for (int i = 0; i < 30; i++)
        {
            var warmup = adapter.CaptureDetachedAsync("test.warmup", () => new(snapshot), CancellationToken.None);
            dispatcher.RunPending();
            Assert.That((await warmup).Payload, Is.EqualTo(expected));
        }

        // Measure just the dispatched callback. Native snapshot capture is identical in
        // both variants, so use the same detached character, not live Terraria globals.
        long baselineBytes = 0, baselineTicks = 0, workerBytes = 0, workerTicks = 0;
        for (int i = 0; i < players; i++)
        {
            string? inline = null;
            Action encodeInline = () => inline = SerializeImportedCharacter(snapshot);
            (long bytes, long ticks) = dispatcher.Measure(encodeInline);
            baselineBytes += bytes;
            baselineTicks += ticks;
            Assert.That(inline, Is.EqualTo(expected));

            var pending = adapter.CaptureDetachedAsync("test.checkpoint", () => new(snapshot), CancellationToken.None);
            (bytes, ticks) = dispatcher.MeasurePending();
            workerBytes += bytes;
            workerTicks += ticks;
            Assert.That((await pending).Payload, Is.EqualTo(expected));
        }

        TestContext.Out.WriteLine($"Game-thread callback, {players} full-size character encodes: " +
            $"{baselineBytes:N0} -> {workerBytes:N0} B, " +
            $"{TimeSpan.FromSeconds((double)baselineTicks / Stopwatch.Frequency).TotalMilliseconds:F3} -> " +
            $"{TimeSpan.FromSeconds((double)workerTicks / Stopwatch.Frequency).TotalMilliseconds:F3} ms; " +
            $"payload={expected.Length:N0} chars. Native snapshot copying excluded.");
        Assert.That(workerBytes, Is.LessThan(baselineBytes / 10));
    }

    private static CharacterState CreateCharacter()
    {
        ItemState[] Slots(int count) => Enumerable.Range(0, count)
            .Select(i => new ItemState(i + 1, i + 1, 0, false)).ToArray();
        return new CharacterState
        {
            Version = 1, Life = 400, MaxLife = 400, Mana = 200, MaxMana = 200,
            Inventory = Slots(59), Armor = Slots(20), Dye = Slots(10), MiscEquips = Slots(5), MiscDyes = Slots(5),
            Piggy = Slots(40), Safe = Slots(40), Forge = Slots(40), VoidVault = Slots(40),
            Loadouts = Enumerable.Range(0, 3).Select(_ => new LoadoutState { Armor = Slots(20), Dye = Slots(10) }).ToArray()
        };
    }

    private sealed class ManualDispatcher : IGameThreadDispatcher
    {
        private Action? pending;
        public bool IsGameThreadKnown => true;
        public bool IsGameThread { get; private set; }
        public void AssertGameThread() => Assert.That(IsGameThread, Is.True);
        public bool TryPost(string name, Action action)
        {
            Assert.That((object?)pending, Is.Null);
            pending = action;
            return true;
        }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
        public void Run(Action action) => Measure(action);
        public void RunPending() => MeasurePending();
        public (long Bytes, long Ticks) MeasurePending()
        {
            Action action = pending ?? throw new InvalidOperationException("No queued action.");
            pending = null;
            return Measure(action);
        }
        public (long Bytes, long Ticks) Measure(Action action)
        {
            IsGameThread = true;
            try
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                action();
                return (GC.GetAllocatedBytesForCurrentThread() - bytes, Stopwatch.GetTimestamp() - start);
            }
            finally { IsGameThread = false; }
        }
    }
}
