namespace Vega.Contracts.Worlds;

public interface IWorldService
{
    IWorldContext Current { get; }

    ValueTask SaveAsync(CancellationToken cancellationToken = default);
}
