namespace Vega.Contracts.Worlds;

/// <summary>Native historical credit, not an association with the player currently occupying a slot.</summary>
public sealed record BossDamageContribution(string Label, string? RecordedCharacterName, int Damage, int HitCount, int Percentage);

public sealed record BossDamageAttempt(string Encounter, int DurationTicks, IReadOnlyList<BossDamageContribution> Contributions);

public sealed record BossDamageHistorySnapshot(WorldId WorldId, IReadOnlyList<BossDamageAttempt> Attempts);

public interface IBossDamageHistory
{
    ValueTask<BossDamageHistorySnapshot?> RecentAsync(WorldId worldId, CancellationToken cancellationToken = default);
}
