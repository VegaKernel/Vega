namespace Vega.Contracts.Worlds;

/// <summary>
/// Raised before Terraria starts the hardmode world transition.
/// Cancellation prevents the underlying Terraria operation from running.
/// </summary>
public readonly record struct WorldHardmodeStarting;
