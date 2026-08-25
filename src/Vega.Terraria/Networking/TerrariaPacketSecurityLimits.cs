namespace Vega.Terraria.Networking;

/// <summary>
/// Concrete Terraria limits supplied to packet policies without leaking Terraria types across the Core boundary.
/// </summary>
public static class TerrariaPacketSecurityLimits
{
    public static int PlayerCount => global::Terraria.Main.maxPlayers;

    public static int PlayerItemSlotCount => global::Terraria.ID.PlayerItemSlotID.Count;

    public static int ItemTypeCount => global::Terraria.ID.ItemID.Count;

    public static int PrefixCount => global::Terraria.ID.PrefixID.Count;

    public static int ProjectileTypeCount => global::Terraria.ID.ProjectileID.Count;

    public static int ProjectileCount => global::Terraria.Main.maxProjectiles + 1;

    public static int NpcCount => global::Terraria.Main.maxNPCs;

    public static int NpcTypeCount => global::Terraria.ID.NPCID.Count;

    public static int BuffTypeCount => global::Terraria.ID.BuffID.Count;

    public static int TileTypeCount => global::Terraria.ID.TileID.Count;

    public static int WallTypeCount => global::Terraria.ID.WallID.Count;

    public static int PaintColorCount => global::Terraria.Main.numTileColors + 1;

    public static int WorldWidth => global::Terraria.Main.maxTilesX;

    public static int WorldHeight => global::Terraria.Main.maxTilesY;

    public static int ChestCount => global::Terraria.Main.chest.Length;

    public static int InventorySlotCount =>
        global::Terraria.Main.player.Length == 0 ? 0 : global::Terraria.Main.player[0]?.inventory.Length ?? 0;
}
