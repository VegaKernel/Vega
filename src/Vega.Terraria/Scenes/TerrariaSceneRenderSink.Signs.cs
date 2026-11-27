using Multiplicity.Packets;
using Terraria.Net.Sockets;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Terraria.Players;

namespace Vega.Terraria.Scenes;

public sealed partial class TerrariaSceneRenderSink : ISceneVirtualSignUiSink
{
    public bool TryGetVirtualSignIndex(PlayerSnapshot player, SceneVirtualSignReadRequest read, out short index)
    {
        index = -1;
        if (!TryGetSignRecipient(player, read.Sign, out _, out _)) return false;
        // Client sign slots are bounded native array indices, not globally allocated fake signs.
        // The scene runtime reserves each selected alias for interception for this exact session.
        for (int i = Math.Min(short.MaxValue, global::Terraria.Main.sign.Length - 1); i >= 0; i--)
            if (global::Terraria.Main.sign[i] is null) { index = (short)i; return true; }
        return false;
    }

    public bool TrySendVirtualSign(PlayerSnapshot player, SceneVirtualSignSnapshot sign, short index, bool open)
    {
        if ((uint)index >= (uint)global::Terraria.Main.sign.Length || global::Terraria.Main.sign[index] is not null ||
            !TryGetSignRecipient(player, sign, out var client, out var socket)) return false;
        try
        {
            byte[] data = EncodeVirtualSign(sign, player.Id.Slot, index, open);
            if (!client.IsActive || !ReferenceEquals(client.Socket, socket) || !ReferenceEquals(global::Terraria.Netplay.Clients[player.Id.Slot], client)) return false;
            socket.AsyncSend(data, 0, data.Length, DirectSendCompleted);
            return true;
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Warning, "scenes", "Virtual sign presentation failed.", ex);
            return false;
        }
    }

    private bool TryGetSignRecipient(PlayerSnapshot player, SceneVirtualSignSnapshot sign, out global::Terraria.RemoteClient client, out ISocket socket)
    {
        client = null!;
        socket = null!;
        int slot = player.Id.Slot, x = sign.Position.X, y = sign.Position.Y;
        if (!dispatcher.IsGameThread || scenes is not { } runtime || player.SessionId is null || player.WorldId != currentWorld || sign.WorldId != currentWorld ||
            x < 0 || y < 0 || x > short.MaxValue - 1 || y > short.MaxValue - 1 || x + 1 >= global::Terraria.Main.maxTilesX || y + 1 >= global::Terraria.Main.maxTilesY ||
            (uint)slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            players is not TerrariaPlayerBoundary boundary || !boundary.TryGetSessionPlayer(player, out var live) || live.dead ||
            !float.IsFinite(live.position.X) || !float.IsFinite(live.position.Y) ||
            Math.Abs(live.position.X - (x * 16d + 16)) > 128 || Math.Abs(live.position.Y - (y * 16d + 16)) > 128 ||
            global::Terraria.Netplay.Clients[slot] is not { IsActive: true, Socket: { } currentSocket } currentClient) return false;
        // A logical sign can be attached to arbitrary active preview tiles. Native UI requires a real sign-shaped visual footprint.
        ushort type = 0;
        short frameX = 0, frameY = 0;
        for (int dx = 0; dx < 2; dx++)
        for (int dy = 0; dy < 2; dy++)
        {
            if (!runtime.TryResolveTile(currentWorld, player.Id, x + dx, y + dy, out var tile, out var overlay) || overlay != sign.OverlayId ||
                (tile.Flags & SceneTileFlags.Active) == 0 || tile.TileType >= global::Terraria.Main.tileSign.Length || !global::Terraria.Main.tileSign[tile.TileType]) return false;
            if (dx == 0 && dy == 0)
            {
                if (tile.FrameX < 0 || tile.FrameY < 0 || tile.FrameX % 36 != 0 || tile.FrameY % 36 != 0) return false;
                type = tile.TileType; frameX = tile.FrameX; frameY = tile.FrameY;
            }
            else if (tile.TileType != type || tile.FrameX != frameX + dx * 18 || tile.FrameY != frameY + dy * 18) return false;
        }
        client = currentClient;
        socket = currentSocket;
        return true;
    }

    internal static byte[] EncodeVirtualSign(SceneVirtualSignSnapshot sign, int playerSlot, short index, bool open)
    {
        using var stream = new MemoryStream();
        new SignNew { SignId = index, X = checked((short)sign.Position.X), Y = checked((short)sign.Position.Y),
            Text = sign.Definition.Text, PlayerId = checked((byte)playerSlot), SuppressOpen = !open }.ToStream(stream);
        return stream.ToArray();
    }
}
