namespace Vega.Contracts.Players;

public readonly record struct PlayerSessionId(Guid Value)
{
	public static PlayerSessionId New() => new(Guid.NewGuid());

	public override string ToString() => Value.ToString("N");
}
