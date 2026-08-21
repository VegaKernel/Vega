namespace Vega.Terraria.Networking;

/// <summary>
/// Concrete Terraria limits supplied to Core packet policies without leaking Terraria types across the boundary.
/// </summary>
public static class TerrariaPacketSecurityLimits
{
    public static int ItemTypeCount => global::Terraria.ID.ItemID.Count;

    public static int PrefixCount => global::Terraria.ID.PrefixID.Count;

    public static int ProjectileTypeCount => global::Terraria.ID.ProjectileID.Count;

    public static int ProjectileCount => global::Terraria.Main.maxProjectiles + 1;

    public static int TileTypeCount => global::Terraria.ID.TileID.Count;

    public static int WallTypeCount => global::Terraria.ID.WallID.Count;

    public static int WorldWidth => global::Terraria.Main.maxTilesX;

    public static int WorldHeight => global::Terraria.Main.maxTilesY;

    public static int ChestCount => global::Terraria.Main.chest.Length;

    public static int InventorySlotCount =>
        global::Terraria.Main.player.Length == 0 ? 0 : global::Terraria.Main.player[0]?.inventory.Length ?? 0;
}
