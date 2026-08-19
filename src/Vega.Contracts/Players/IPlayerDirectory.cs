namespace TShockNG.Contracts.Players;

/// <summary>
/// Provides immutable snapshots of currently connected players without exposing Terraria objects.
/// </summary>
public interface IPlayerDirectory
{
    IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers();

    bool TryGet(PlayerId id, out PlayerSnapshot? player);

    bool TryFindExact(string name, out PlayerSnapshot? player);
}
