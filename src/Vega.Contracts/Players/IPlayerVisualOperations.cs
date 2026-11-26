namespace Vega.Contracts.Players;

public enum FireworkStyle { Red, Green, Blue, Yellow, Star, Spiral, Rings, Flower }

public interface IPlayerVisualOperations
{
    ValueTask<PlayerOperationResult> AnnoyAsync(PlayerSnapshot player, int seconds, CancellationToken cancellationToken = default);
    ValueTask<PlayerOperationResult> FireworkAsync(PlayerSnapshot player, FireworkStyle style, CancellationToken cancellationToken = default);
}

public interface IPlayerGodModeOperations
{
    /// <summary>Toggle on null; otherwise set an explicit native power state.</summary>
    ValueTask<(PlayerOperationResult Result, bool Enabled)> SetGodModeAsync(PlayerSnapshot player, bool? enabled,
        CancellationToken cancellationToken = default);
}
