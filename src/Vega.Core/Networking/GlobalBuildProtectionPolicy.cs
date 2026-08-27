using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Authorization;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class RuntimeWorldBuildProtectionService : IWorldBuildProtectionService
{
    private int enabled;

    public bool IsEnabled => Volatile.Read(ref enabled) != 0;

    public bool Toggle()
    {
        while (true)
        {
            int current = Volatile.Read(ref enabled);
            int next = current == 0 ? 1 : 0;
            if (Interlocked.CompareExchange(ref enabled, next, current) == current)
            {
                return next != 0;
            }
        }
    }
}

public sealed class GlobalBuildProtectionPolicy(
    IWorldBuildProtectionService protection,
    IPlayerAuthorizationService authorization) : IPacketProcessor<PacketView>
{
    public const string BypassPermission = "vega.world.antibuild";

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            !protection.IsEnabled ||
            !IsBuildMutation(context.Packet.PacketType))
        {
            return PacketProcessingDecision.Allow;
        }

        if (context.PlayerId is not PlayerId playerId)
        {
            return PacketProcessingDecision.Reject("packet.antibuild.player_unavailable");
        }

        return authorization.HasPermission(playerId, BypassPermission)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("packet.antibuild.build_denied");
    }

    private static bool IsBuildMutation(PacketTypes packetType) => packetType is
        PacketTypes.Tile or
        PacketTypes.TileSendSquare or
        PacketTypes.PaintTile or
        PacketTypes.PaintWall or
        PacketTypes.SignNew or
        PacketTypes.PlaceChest or
        PacketTypes.PlaceObject or
        PacketTypes.PlaceTileEntity or
        PacketTypes.PlaceItemFrame or
        PacketTypes.LiquidSet or
        PacketTypes.GemLockToggle or
        PacketTypes.MassWireOperation or
        PacketTypes.MassWireOperationPay or
        PacketTypes.TileEntityDisplayDollItemSync or
        PacketTypes.WeaponsRackTryPlacing or
        PacketTypes.TileEntityHatRackItemSync or
        PacketTypes.FoodPlatterTryPlacing or
        PacketTypes.TEDeadCellsDisplayJar or
        PacketTypes.TELeashedEntityAnchorPlaceItem;
}
