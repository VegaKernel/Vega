namespace Vega.Contracts.Players;

public sealed record PlayerCombatSnapshot(
	PlayerId PlayerId,
	short HeldItemType,
	byte HeldItemPrefix,
	bool UsesArrowAmmo);

public interface IPlayerCombatDirectory
{
	bool TryGetCombatSnapshot(PlayerId playerId, out PlayerCombatSnapshot? snapshot);
}
