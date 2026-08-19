namespace TShockNG.Contracts.Players;

public interface IPlayerService
{
    IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers();

    bool TryGet(PlayerId id, out PlayerSnapshot? player);
}
