using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects malformed teleport operations and coordinates before Terraria applies them.
/// Terraria replaces client-authored player entity ids with the sending connection, so only NPC targets
/// are range-checked here.
/// </summary>
public sealed class TeleportSanityPolicy(
    int npcCount,
    Func<int> worldWidth,
    Func<int> worldHeight) : IPacketProcessor<PacketView>
{
    private const int TileSize = 16;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound || context.Packet.PacketType != PacketTypes.Teleport)
        {
            return PacketProcessingDecision.Allow;
        }

        var teleport = new TeleportView(context.Packet);
        if (!Enum.IsDefined(teleport.Operation))
        {
            return PacketProcessingDecision.Violation("packet.teleport.operation_out_of_range");
        }

        // Operation 1 is the NPC teleport operation in Terraria's two-bit teleport type field.
        if ((int)teleport.Operation == 1 && (teleport.TargetId < 0 || teleport.TargetId >= npcCount))
        {
            return PacketProcessingDecision.Violation("packet.teleport.npc_target_out_of_range");
        }

        if (teleport.UseCurrentEntityPosition)
        {
            return PacketProcessingDecision.Allow;
        }

        if (!float.IsFinite(teleport.X) || !float.IsFinite(teleport.Y))
        {
            return PacketProcessingDecision.Violation("packet.teleport.non_finite_position");
        }

        int width = worldWidth();
        int height = worldHeight();
        return width > 0 && height > 0 &&
            (teleport.X < 0 || teleport.Y < 0 ||
             teleport.X > width * TileSize || teleport.Y > height * TileSize)
            ? PacketProcessingDecision.Violation("packet.teleport.position_out_of_world")
            : PacketProcessingDecision.Allow;
    }
}
