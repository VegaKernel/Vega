namespace Vega.Contracts.Worlds;

/// <summary>Controls runtime-wide and spawn-area player world-mutation protection.</summary>
public interface IWorldBuildProtectionService
{
    bool IsEnabled { get; }

    bool IsSpawnProtectionEnabled { get; }

    int SpawnProtectionRadiusTiles { get; }

    bool Toggle();

    bool ToggleSpawnProtection(int spawnTileX, int spawnTileY, int radiusTiles);

    void UpdateSpawnPosition(int spawnTileX, int spawnTileY);

    bool IsSpawnProtected(int tileX, int tileY);
}