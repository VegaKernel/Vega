using Terraria.GameContent;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

/// <summary>Copies the vanilla damage tracker on its owning world thread.</summary>
public sealed class TerrariaBossDamageHistory(IGameThreadDispatcher dispatcher, WorldId worldId) : IBossDamageHistory
{
    public ValueTask<BossDamageHistorySnapshot?> RecentAsync(WorldId requestedWorld, CancellationToken cancellationToken = default)
    {
        if (requestedWorld != worldId || cancellationToken.IsCancellationRequested || !dispatcher.IsGameThreadKnown)
            return ValueTask.FromResult<BossDamageHistorySnapshot?>(null);
        if (dispatcher.IsGameThread) return ValueTask.FromResult<BossDamageHistorySnapshot?>(Capture());
        var completion = new TaskCompletionSource<BossDamageHistorySnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("world.boss-damage", () =>
        {
            try { completion.TrySetResult(cancellationToken.IsCancellationRequested ? null : Capture()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) return ValueTask.FromResult<BossDamageHistorySnapshot?>(null);
        return new(completion.Task.WaitAsync(cancellationToken));
    }

    private BossDamageHistorySnapshot Capture()
    {
        dispatcher.AssertGameThread();
        var attempts = new List<BossDamageAttempt>();
        foreach (NPCDamageTracker encounter in NPCDamageTracker.RecentAttempts())
        {
            // NPCDamageTracker.GetOrAddEntry stores the character name at hit time;
            // never remap this credit to whichever player later occupies a slot.
            var entries = encounter._list.OrderByDescending(entry => entry.Damage).ToArray();
            if (entries.Length == 0) continue;
            var damage = entries.Select(entry => entry.Damage).ToArray();
            int[] percentages = damage.Any(value => value > 0)
                ? NPCDamageTracker.CalculatePercentages(damage)
                : new int[entries.Length];
            var contributions = new BossDamageContribution[entries.Length];
            for (int index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                contributions[index] = new(entry.Name.ToString(), (entry as NPCDamageTracker.PlayerCreditEntry)?.PlayerName,
                    entry.Damage, entry.HitCount, percentages[index]);
            }
            attempts.Add(new(encounter.Name.Value, encounter.Duration, contributions));
        }
        return new(worldId, attempts);
    }
}
