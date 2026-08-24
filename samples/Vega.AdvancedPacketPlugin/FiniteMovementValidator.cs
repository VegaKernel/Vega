using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.AdvancedPacketPlugin;

/// <summary>
/// Advanced opt-in example. PURE / NO TERRARIA MUTABLE STATE / NO BLOCKING I/O /
/// MAY RUN CONCURRENTLY. It retains no PacketView or receive-buffer reference.
/// </summary>
public sealed class FiniteMovementValidator : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.Packet.PacketType != PacketTypes.PlayerUpdate)
        {
            return PacketProcessingDecision.Allow;
        }

        PlayerUpdateView movement = context.Packet.AsPlayerUpdateView();
        return float.IsFinite(movement.PositionX) &&
               float.IsFinite(movement.PositionY) &&
               float.IsFinite(movement.VelocityX) &&
               float.IsFinite(movement.VelocityY)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Violation("sample.player.non_finite_movement");
    }
}
