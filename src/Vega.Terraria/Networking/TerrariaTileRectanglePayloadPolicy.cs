using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using TileModel = Multiplicity.Packets.Models.Tile;

namespace Vega.Terraria.Networking;

/// <summary>
/// Fully decodes client-authored tile rectangle payloads so malformed compression/tile ids cannot reach Terraria.
/// </summary>
public sealed class TerrariaTileRectanglePayloadPolicy : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || context.Packet.PacketType != PacketTypes.TileSendSquare)
        {
            return PacketProcessingDecision.Allow;
        }

        try
        {
            var view = new TileSendSquareView(context.Packet);
            TileModel[,] tiles = view.ReadTiles(IsFrameImportant);
            for (int x = 0; x < tiles.GetLength(0); x++)
            {
                for (int y = 0; y < tiles.GetLength(1); y++)
                {
                    TileModel tile = tiles[x, y];
                    if (tile.IsActive && tile.Type >= global::Terraria.ID.TileID.Count)
                    {
                        return PacketProcessingDecision.Violation("packet.tile_square.tile_type_out_of_range");
                    }

                    if (tile.HasWall && tile.Wall >= global::Terraria.ID.WallID.Count)
                    {
                        return PacketProcessingDecision.Violation("packet.tile_square.wall_type_out_of_range");
                    }

                    if (tile.HasTileColor && tile.TileColor > global::Terraria.Main.numTileColors)
                    {
                        return PacketProcessingDecision.Violation("packet.tile_square.tile_color_out_of_range");
                    }

                    if (tile.HasWallColor && tile.WallColor > global::Terraria.Main.numTileColors)
                    {
                        return PacketProcessingDecision.Violation("packet.tile_square.wall_color_out_of_range");
                    }
                }
            }

            return PacketProcessingDecision.Allow;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            return PacketProcessingDecision.Reject("packet.tile_square.malformed_payload");
        }
    }

    private static bool IsFrameImportant(ushort type) =>
        type < global::Terraria.Main.tileFrameImportant.Length &&
        global::Terraria.Main.tileFrameImportant[type];
}
