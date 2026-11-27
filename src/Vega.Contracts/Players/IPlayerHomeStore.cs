using Vega.Contracts.Accounts;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Players;

/// <summary>Personal teleport points are owned by an account and the exact logical and physical world.</summary>
public sealed record PlayerHome(AccountId AccountId, WorldId WorldId, Guid WorldFileId, string Name, PlayerPosition Position)
{
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 32 } && name == name.Trim() &&
        !name.Any(char.IsControl) && !name.Equals("-l", StringComparison.OrdinalIgnoreCase);
}

public enum HomeWriteResult { Applied, LimitReached, AccountNotFound }

public interface IPlayerHomeStore
{
    ValueTask<IReadOnlyList<PlayerHome>> ListAsync(AccountId account, WorldId world, Guid worldFile, CancellationToken token = default);
    ValueTask<PlayerHome?> FindAsync(AccountId account, WorldId world, Guid worldFile, string name, CancellationToken token = default);
    ValueTask<HomeWriteResult> SetAsync(PlayerHome home, int limit, CancellationToken token = default);
    ValueTask<bool> DeleteAsync(AccountId account, WorldId world, Guid worldFile, string name, CancellationToken token = default);
}
