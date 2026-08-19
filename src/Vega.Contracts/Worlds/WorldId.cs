namespace TShockNG.Contracts.Worlds;

/// <summary>
/// Stable identifier of a managed world context.
/// </summary>
public readonly record struct WorldId(string Value)
{
    public static readonly WorldId Primary = new("primary");

    public override string ToString() => Value;
}
