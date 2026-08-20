namespace Vega.Contracts.Runtime;

/// <summary>
/// Initializes one required runtime capability after cheap module registration and before world loading.
/// </summary>
public interface IRuntimeStartupParticipant
{
    string Name { get; }

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
