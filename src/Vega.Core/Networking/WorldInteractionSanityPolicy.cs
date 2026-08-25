using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Covers modern client world-interaction packets that are outside the classic tile/chest mutation policy.
/// </summary>
public sealed class WorldInteractionSanityPolicy(
    Func<int> worldWidth,
    Func<int> worldHeight,
    int itemTypeCount,
    int prefixCount,
    int projectileTypeCount) : IPacketProcessor<PacketView>
{
    private const int MaximumStack = 9_999;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.TileSendSquare => ValidateTileRectangle(new TileSendSquareView(context.Packet)),
            PacketTypes.HitSwitch => ValidateCoordinates(
                new HitSwitchView(context.Packet).X,
                new HitSwitchView(context.Packet).Y,
                "packet.switch.position_out_of_world"),
            PacketTypes.WeaponsRackTryPlacing => ValidateWeaponRack(new WeaponsRackTryPlacingView(context.Packet)),
            PacketTypes.WiredCannonShot => ValidateWiredCannon(new WiredCannonShotView(context.Packet)),
            PacketTypes.MassWireOperationPay => ValidateMassWirePayment(new MassWireOperationPayView(context.Packet)),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateTileRectangle(TileSendSquareView value)
    {
        int width = worldWidth();
        int height = worldHeight();
        if (width <= 0 || height <= 0 || value.Width == 0 || value.Height == 0 ||
            value.TileX < 0 || value.TileY < 0 ||
            value.TileX + value.Width > width || value.TileY + value.Height > height)
        {
            return PacketProcessingDecision.Violation("packet.tile_square.rectangle_out_of_world");
        }

        return PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateWeaponRack(WeaponsRackTryPlacingView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(
            value.TileX,
            value.TileY,
            "packet.weapon_rack.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        if (value.ItemType < 0 || value.ItemType >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.weapon_rack.item_type_out_of_range");
        }

        if (value.Prefix >= prefixCount)
        {
            return PacketProcessingDecision.Violation("packet.weapon_rack.prefix_out_of_range");
        }

        return value.Stack is < 0 or > MaximumStack
            ? PacketProcessingDecision.Violation("packet.weapon_rack.stack_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateWiredCannon(WiredCannonShotView value)
    {
        PacketProcessingDecision position = ValidateCoordinates(
            value.X,
            value.Y,
            "packet.wired_cannon.position_out_of_world");
        if (position.Kind != PacketDecisionKind.Allow)
        {
            return position;
        }

        if (value.Damage < 0 || !float.IsFinite(value.Knockback))
        {
            return PacketProcessingDecision.Violation("packet.wired_cannon.invalid_combat_values");
        }

        return value.Ammo < 0 || value.Ammo >= projectileTypeCount
            ? PacketProcessingDecision.Violation("packet.wired_cannon.ammo_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateMassWirePayment(MassWireOperationPayView value)
    {
        if (value.ItemType < 0 || value.ItemType >= itemTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.mass_wire.item_type_out_of_range");
        }

        return value.Quantity < 0
            ? PacketProcessingDecision.Violation("packet.mass_wire.quantity_out_of_range")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateCoordinates(int x, int y, string reason)
    {
        int width = worldWidth();
        int height = worldHeight();
        return width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height
            ? PacketProcessingDecision.Violation(reason)
            : PacketProcessingDecision.Allow;
    }
}
