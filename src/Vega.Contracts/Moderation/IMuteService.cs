namespace Vega.Contracts.Moderation;

public enum MuteSubjectKind
{
    PlayerName,
    Account
}

public readonly record struct MuteId(Guid Value)
{
    public static MuteId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public sealed record MuteSnapshot(
    MuteId Id,
    MuteSubjectKind SubjectKind,
    string SubjectValue,
    string Reason,
    string IssuedBy,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    long Revision);

public sealed record MuteMutationResult(
    bool Succeeded,
    string Code,
    MuteSnapshot? Before = null,
    MuteSnapshot? After = null);

public interface IMuteService
{
    IReadOnlyList<MuteSnapshot> SnapshotActive(DateTimeOffset now);

    MuteSnapshot? FindActive(
        string playerName,
        string? accountName,
        DateTimeOffset now);

    ValueTask<MuteMutationResult> CreateAsync(
        MuteSnapshot mute,
        CancellationToken cancellationToken = default);

    ValueTask<MuteMutationResult> RevokeAsync(
        MuteId muteId,
        long expectedRevision,
        string actor,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken = default);
}
