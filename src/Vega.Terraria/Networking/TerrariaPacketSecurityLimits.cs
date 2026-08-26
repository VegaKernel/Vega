namespace Vega.Terraria.Networking;

/// <summary>
/// Concrete Terraria limits supplied to packet policies without leaking Terraria types across the Core boundary.
/// Protocol-fixed limits must not touch <c>Terraria.Main</c>: the policy graph is composed before Terraria's save path
/// is initialized, and forcing the Main type initializer at that point aborts clean deployment startup.
/// </summary>
public static class TerrariaPacketSecurityLimits
{
    // Terraria 1.4.5.8 / protocol 326 fixed runtime capacities. Keep these explicit so packet-policy
    // construction stays independent from Terraria.Main static initialization.
    public const int PlayerCount = 255;

    public static int PlayerItemSlotCount => global::Terraria.ID.PlayerItemSlotID.Count;

    public static int ItemTypeCount => global::Terraria.ID.ItemID.Count;

    public static int PrefixCount => global::Terraria.ID.PrefixID.Count;

    public static int ProjectileTypeCount => global::Terraria.ID.ProjectileID.Count;

    // Main.projectile has maxProjectiles + 1 entries; security policies intentionally validate against
    // the same count used by the previous live getter.
    public const int ProjectileCount = 1001;

    public const int NpcCount = 200;

    public static int NpcTypeCount => global::Terraria.ID.NPCID.Count;

    public static int BuffTypeCount => global::Terraria.ID.BuffID.Count;

    public static int TileTypeCount => global::Terraria.ID.TileID.Count;

    public static int WallTypeCount => global::Terraria.ID.WallID.Count;

    // Main.numTileColors is 31 for this protocol baseline; valid packet values include zero.
    public const int PaintColorCount = 32;

    // World-dependent values are intentionally lazy. Their delegates are evaluated only after Terraria startup.
    public static int WorldWidth => global::Terraria.Main.maxTilesX;

    public static int WorldHeight => global::Terraria.Main.maxTilesY;

    public static int ChestCount => global::Terraria.Main.chest.Length;

    public static int InventorySlotCount =>
        global::Terraria.Main.player.Length == 0 ? 0 : global::Terraria.Main.player[0]?.inventory.Length ?? 0;
}
