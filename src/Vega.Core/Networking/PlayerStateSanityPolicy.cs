using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Enforces protocol-safe player movement state before Terraria indexes or applies it.
/// </summary>
public sealed class PlayerStateSanityPolicy(
    Func<int> worldWidth,
    Func<int> worldHeight,
    Func<int> inventorySlotCount) : IPacketProcessor<PacketView>
{
    private const float MaximumAbsoluteVelocity = 50_000f;
    private const int TileSize = 16;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        if (context.Packet.PacketType == PacketTypes.PlayerTeleportPortal)
        {
            return ValidatePortal(context.Packet.AsPlayerTeleportPortalView());
        }

        if (context.Packet.PacketType != PacketTypes.PlayerUpdate)
        {
            return PacketProcessingDecision.Allow;
        }

        PlayerUpdateView player = context.Packet.AsPlayerUpdateView();
        if (!float.IsFinite(player.PositionX) || !float.IsFinite(player.PositionY))
        {
            return PacketProcessingDecision.Violation("packet.player.non_finite_position");
        }

        if (!float.IsFinite(player.VelocityX) || !float.IsFinite(player.VelocityY) ||
            MathF.Abs(player.VelocityX) > MaximumAbsoluteVelocity ||
            MathF.Abs(player.VelocityY) > MaximumAbsoluteVelocity)
        {
            return PacketProcessingDecision.Violation("packet.player.invalid_velocity");
        }

        int width = worldWidth();
        int height = worldHeight();
        if (width > 0 && height > 0 &&
            (player.PositionX < 0 || player.PositionY < 0 ||
             player.PositionX >= width * TileSize - TileSize ||
             player.PositionY >= height * TileSize - TileSize))
        {
            return PacketProcessingDecision.Violation("packet.player.position_out_of_world");
        }

        int slots = inventorySlotCount();
        return slots > 0 && player.SelectedItem >= slots
            ? PacketProcessingDecision.Violation("packet.player.selected_item_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidatePortal(PlayerTeleportPortalView portal)
    {
        if (portal.PortalColorIndex is < 0 or > 511)
        {
            return PacketProcessingDecision.Violation("packet.portal.color_out_of_range");
        }

        if (!float.IsFinite(portal.NewPositionX) || !float.IsFinite(portal.NewPositionY))
        {
            return PacketProcessingDecision.Violation("packet.portal.non_finite_position");
        }

        if (!float.IsFinite(portal.VelocityX) || !float.IsFinite(portal.VelocityY))
        {
            return PacketProcessingDecision.Violation("packet.portal.non_finite_velocity");
        }

        int width = worldWidth();
        int height = worldHeight();
        return width > 0 && height > 0 &&
            (portal.NewPositionX < 0 || portal.NewPositionY < 0 ||
             portal.NewPositionX > width * TileSize || portal.NewPositionY > height * TileSize)
            ? PacketProcessingDecision.Violation("packet.portal.position_out_of_world")
            : PacketProcessingDecision.Allow;
    }
}
