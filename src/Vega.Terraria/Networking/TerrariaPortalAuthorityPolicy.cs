using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Terraria.Networking;

/// <summary>
/// Validates portal packets against Terraria's live portal projectiles.
/// </summary>
public sealed class TerrariaPortalAuthorityPolicy : IPacketProcessor<PacketView>
{
    private const float MaximumExitDistance = 128f;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.KillPortal => ValidateKill(new KillPortalView(context.Packet)),
            PacketTypes.PlayerTeleportPortal => ValidatePlayerTeleport(new PlayerTeleportPortalView(context.Packet)),
            _ => PacketProcessingDecision.Allow
        };
    }

    private static PacketProcessingDecision ValidateKill(KillPortalView value) =>
        value.PlayerId >= global::Terraria.Main.maxPlayers || value.PortalColor > 1
            ? PacketProcessingDecision.Violation("packet.portal.invalid_key")
            : PacketProcessingDecision.Allow;

    private static PacketProcessingDecision ValidatePlayerTeleport(PlayerTeleportPortalView value)
    {
        if (!float.IsFinite(value.NewPositionX) ||
            !float.IsFinite(value.NewPositionY) ||
            !float.IsFinite(value.VelocityX) ||
            !float.IsFinite(value.VelocityY))
        {
            return PacketProcessingDecision.Violation("packet.portal.non_finite_transform");
        }

        if (value.PortalColorIndex is < 0 or > 511)
        {
            return PacketProcessingDecision.Violation("packet.portal.color_out_of_range");
        }

        int portalOwner = value.PortalColorIndex / 2;
        int portalSide = value.PortalColorIndex & 1;
        if (portalOwner >= global::Terraria.Main.maxPlayers)
        {
            return PacketProcessingDecision.Violation("packet.portal.owner_out_of_range");
        }

        global::Terraria.Projectile? exitPortal = null;
        global::Terraria.Projectile? entryPortal = null;
        foreach (global::Terraria.Projectile projectile in global::Terraria.Main.projectile)
        {
            if (projectile is null || !projectile.active ||
                projectile.type != global::Terraria.ID.ProjectileID.PortalGunGate ||
                projectile.owner != portalOwner)
            {
                continue;
            }

            int side = (int)projectile.ai[1];
            if (side == portalSide)
            {
                exitPortal = projectile;
            }
            else if (side == 1 - portalSide)
            {
                entryPortal = projectile;
            }

            if (exitPortal is not null && entryPortal is not null)
            {
                break;
            }
        }

        if (exitPortal is null || entryPortal is null)
        {
            return PacketProcessingDecision.Violation("packet.portal.missing_pair");
        }

        float dx = value.NewPositionX - exitPortal.Center.X;
        float dy = value.NewPositionY - exitPortal.Center.Y;
        return dx * dx + dy * dy > MaximumExitDistance * MaximumExitDistance
            ? PacketProcessingDecision.Violation("packet.portal.exit_position_mismatch")
            : PacketProcessingDecision.Allow;
    }
}
