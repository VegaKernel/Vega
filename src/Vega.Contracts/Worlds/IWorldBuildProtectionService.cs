namespace Vega.Contracts.Worlds;

/// <summary>Controls runtime-wide player world-mutation protection.</summary>
public interface IWorldBuildProtectionService
{
    bool IsEnabled { get; }

    bool Toggle();
}
