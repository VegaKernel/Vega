using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects client-authored projectile state that cannot be valid for the sending connection.
/// </summary>
public sealed class ProjectileSpawnSanityPolicy(int projectileCount, int projectileTypeCount) : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound)
        {
            return PacketProcessingDecision.Allow;
        }

        return context.Packet.PacketType switch
        {
            PacketTypes.ProjectileNew => ValidateNew(context.Packet.AsProjectileNewView(), context.ConnectionId.Slot),
            PacketTypes.ProjectileDestroy => ValidateDestroy(context.Packet.AsProjectileDestroyView(), context.ConnectionId.Slot),
            _ => PacketProcessingDecision.Allow
        };
    }

    private PacketProcessingDecision ValidateNew(ProjectileNewView projectile, int connectionSlot)
    {
        PacketProcessingDecision key = ValidateKey(projectile.Key, connectionSlot);
        if (key.Kind != PacketDecisionKind.Allow)
        {
            return key;
        }

        if (projectile.Type < 0 || projectile.Type >= projectileTypeCount)
        {
            return PacketProcessingDecision.Violation("packet.projectile.type_out_of_range");
        }

        if (!float.IsFinite(projectile.PositionX) || !float.IsFinite(projectile.PositionY))
        {
            return PacketProcessingDecision.Violation("packet.projectile.non_finite_position");
        }

        if (!float.IsFinite(projectile.VelocityX) || !float.IsFinite(projectile.VelocityY))
        {
            return PacketProcessingDecision.Violation("packet.projectile.non_finite_velocity");
        }

        return PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateDestroy(ProjectileDestroyView projectile, int connectionSlot)
    {
        PacketProcessingDecision key = ValidateKey(projectile.Key, connectionSlot);
        if (key.Kind != PacketDecisionKind.Allow)
        {
            return key;
        }

        return !float.IsFinite(projectile.PositionX) || !float.IsFinite(projectile.PositionY)
            ? PacketProcessingDecision.Violation("packet.projectile.non_finite_position")
            : PacketProcessingDecision.Allow;
    }

    private PacketProcessingDecision ValidateKey(ProjectileKey key, int connectionSlot)
    {
        if (key.Spawner != connectionSlot)
        {
            return PacketProcessingDecision.Violation("packet.projectile.owner_mismatch");
        }

        return key.Index >= projectileCount
            ? PacketProcessingDecision.Violation("packet.projectile.index_out_of_range")
            : PacketProcessingDecision.Allow;
    }
}
