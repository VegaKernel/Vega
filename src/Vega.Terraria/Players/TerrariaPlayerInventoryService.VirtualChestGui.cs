using Multiplicity.Packets;
using Terraria.ID;
using Terraria.Net.Sockets;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Players;

public sealed partial class TerrariaPlayerInventoryService : IVirtualChestGuiHost
{
    private static readonly SocketSendCallback GuiSendCompleted = static _ => { };

    public bool TryPresentManagedGui(ISceneRuntime scenes, SceneVirtualChestOpenRequest open)
    {
        if (scenes is not IVirtualChestGuiAuthority authority || !TryGetGuiPlayer(open.Observer, open.Chest.Identity.WorldId, out var expected, out var live) ||
            live.dead || live.chest >= 0 || !authority.CanPresentManagedGui(expected, open.Chest.Identity) ||
            global::Terraria.Chest.FindChest(open.Chest.Identity.X, open.Chest.Identity.Y) >= 0 ||
            !HasGuiFootprint(scenes, expected, open.Chest) || !TryValidateGuiContents(open.Chest.Definition)) return false;
        short index = -1;
        for (int i = Math.Min(short.MaxValue, global::Terraria.Main.chest.Length - 1); i >= 0; i--)
            if (global::Terraria.Main.chest[i] is null && authority.CanUseManagedGuiIndex(expected, (short)i, open.Chest.Identity)) { index = (short)i; break; }
        if (index < 0 || Capture(expected) is not { } inventory || !authority.TryBeginManagedGui(expected, open, index, inventory, this)) return false;
        if (TrySendGuiContents(expected, open.Chest, index, openWindow: true)) return true;
        // A send may have partially completed. Keep the exact session managed and close rather than
        // restoring ordinary client-written inventory without reconciliation.
        authority.RequestManagedGuiClose(expected);
        return false;
    }

    public SceneVirtualChestTransferResult AcceptManagedGuiTransfer(ISceneRuntime scenes, SceneVirtualChestGuiTransferIntent intent)
    {
        if (!dispatcher.IsGameThread || scenes is not IVirtualChestGuiAuthority authority ||
            !authority.TryClaimManagedGuiTransfer(intent, out var transfer)) return new(VirtualChestTransferStatus.StaleInteraction);
        var result = TransferVirtualChestCore(scenes, transfer, allowMouse: true);
        var observer = transfer.Open.Observer;
        PlayerInventorySnapshot? inventory = null;
        PlayerSnapshot? player = null;
        if (TryGetGuiPlayer(observer, transfer.Open.Chest.Identity.WorldId, out var expected, out _))
        { player = expected; inventory = Capture(expected); }
        bool refresh = authority.CompleteManagedGuiTransfer(intent, result, inventory, out short index, out var chest);
        if (result.Status == VirtualChestTransferStatus.Applied && result.Error is not null && player is not null)
        {
            authority.RequestManagedGuiClose(player);
            return result;
        }
        if (refresh && player is not null && chest is { } current && !TrySendGuiContents(player, current, index, openWindow: false))
        {
            authority.RequestManagedGuiClose(player);
            return result with { Error = "virtual_chest.sync_failed" };
        }
        return result;
    }

    public bool SynchronizeManagedGui(PlayerSnapshot player, short index, ChestIdentity? closedChest, IReadOnlyList<int> inventoryCorrections)
    {
        if (player.SessionId is not { } session || !TryGetGuiPlayer(new(player.Id, session), player.WorldId, out var expected, out _)) return false;
        try
        {
            if (closedChest is { } closed)
            {
                using var stream = new MemoryStream();
                new ChestOpen { ChestId = -1, ChestX = checked((short)closed.X), ChestY = checked((short)closed.Y) }.ToStream(stream);
                TrySendGuiBytes(expected, stream.ToArray());
            }
            foreach (int slot in inventoryCorrections)
                if ((uint)slot < (uint)TerrariaPacketSecurityLimits.CharacterSlotCount)
                    global::Terraria.NetMessage.SendData(MessageID.SyncEquipment, expected.Id.Slot, -1, null, expected.Id.Slot, slot);
            return true;
        }
        catch (Exception) { return false; }
    }

    private bool TryGetGuiPlayer(SceneSessionObserver observer, WorldId world, out PlayerSnapshot expected, out global::Terraria.Player live)
    {
        expected = null!; live = null!;
        if (!dispatcher.IsGameThread || !global::Terraria.Main.ServerSideCharacter || !players.TryGet(observer.Player, out var snapshot) ||
            snapshot is null || snapshot.SessionId != observer.Session || snapshot.WorldId != world || !players.TryGetSessionPlayer(snapshot, out live)) return false;
        expected = snapshot with { Position = new(live.position.X, live.position.Y), IsDead = live.dead };
        return float.IsFinite(live.position.X) && float.IsFinite(live.position.Y);
    }

    private static bool HasGuiFootprint(ISceneRuntime scenes, PlayerSnapshot player, SceneVirtualChestSnapshot chest)
    {
        int x = chest.Identity.X, y = chest.Identity.Y;
        if (x < 0 || y < 0 || x > short.MaxValue - 1 || y > short.MaxValue - 1 || x + 1 >= global::Terraria.Main.maxTilesX || y + 1 >= global::Terraria.Main.maxTilesY) return false;
        ushort type = 0; short frameX = 0, frameY = 0;
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
        {
            if (!scenes.TryResolveTile(player.WorldId, player.Id, x + dx, y + dy, out var tile, out var overlay) || overlay != chest.OverlayId ||
                (tile.Flags & SceneTileFlags.Active) == 0 || tile.TileType is not (TileID.Containers or TileID.Containers2)) return false;
            if (dx == 0 && dy == 0)
            {
                if (tile.FrameX < 0 || tile.FrameY < 0 || tile.FrameX % 36 != 0 || tile.FrameY % 36 != 0) return false;
                type = tile.TileType; frameX = tile.FrameX; frameY = tile.FrameY;
            }
            else if (tile.TileType != type || tile.FrameX != frameX + dx * 18 || tile.FrameY != frameY + dy * 18) return false;
        }
        return true;
    }

    private static bool TryValidateGuiContents(SceneVirtualChestDefinition definition)
    {
        foreach (var value in definition.Items)
        {
            if (value.ItemType == 0) continue;
            if (value.ItemType < 0 || value.ItemType >= ItemID.Count || value.Prefix >= PrefixID.Count) return false;
            var item = new global::Terraria.Item();
            item.SetDefaults(value.ItemType);
            if (item.type != value.ItemType || value.Stack > item.maxStack || value.Prefix != 0 && !item.Prefix(value.Prefix)) return false;
        }
        return true;
    }

    private bool TrySendGuiContents(PlayerSnapshot player, SceneVirtualChestSnapshot chest, short index, bool openWindow)
    {
        if ((uint)index >= (uint)global::Terraria.Main.chest.Length || global::Terraria.Main.chest[index] is not null ||
            global::Terraria.Chest.FindChest(chest.Identity.X, chest.Identity.Y) >= 0 || !TryValidateGuiContents(chest.Definition)) return false;
        try { return TrySendGuiBytes(player, EncodeManagedGui(chest, index, openWindow)); }
        catch (Exception) { return false; }
    }

    private static bool TrySendGuiBytes(PlayerSnapshot player, byte[] bytes)
    {
        int slot = player.Id.Slot;
        if ((uint)slot >= (uint)global::Terraria.Netplay.Clients.Length || global::Terraria.Netplay.Clients[slot] is not { IsActive: true, Socket: { } socket } client) return false;
        if (!client.IsActive || !ReferenceEquals(client.Socket, socket) || !ReferenceEquals(global::Terraria.Netplay.Clients[slot], client)) return false;
        socket.AsyncSend(bytes, 0, bytes.Length, GuiSendCompleted);
        return true;
    }

    internal static byte[] EncodeManagedGui(SceneVirtualChestSnapshot chest, short index, bool openWindow)
    {
        using var stream = new MemoryStream();
        string name = chest.Definition.Name;
        int length = Math.Min(name.Length, ChestOpen.MaxChestNameLength);
        if (length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
        name = name[..length];
        // Stock clients discard item updates for absent chest entries. The ordinary
        // chest metadata response creates the client-only entry before its 40 slots.
        new ChestName { ChestId = index, ChestX = checked((short)chest.Identity.X), ChestY = checked((short)chest.Identity.Y), Name = name }.ToStream(stream);
        for (int slot = 0; slot < SceneVirtualChestDefinition.SlotCount; slot++)
        {
            var item = chest.Definition.Items.FirstOrDefault(value => value.Slot == slot);
            new ChestItem { ChestId = index, ItemSlot = (byte)slot, ItemNetId = checked((short)item.ItemType), Stack = checked((short)item.Stack), Prefix = item.Prefix }.ToStream(stream);
        }
        if (openWindow)
        {
            new ChestOpen { ChestId = index, ChestX = checked((short)chest.Identity.X), ChestY = checked((short)chest.Identity.Y), ChestName = name }.ToStream(stream);
        }
        return stream.ToArray();
    }
}
