namespace TShockNG.Contracts.Regions;

public interface IRegionService
{
    IReadOnlyList<RegionSnapshot> SnapshotRegions();

    RegionSnapshot? FindTopRegion(int tileX, int tileY);
}
