using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects client-authored projectile state that cannot be valid for the sending connection.
/// </summary>
public sealed class ProjectileSpawnSanityPolicy : IPacketProcessor<PacketView>
{
    private const ushort MaximumProjectileIndex = 1000;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.Packet.PacketType != PacketTypes.ProjectileNew)
        {
            return Continue();
        }

        var projectile = new ProjectileNewView(context.Packet);
        if (projectile.Key.Spawner != context.ConnectionId.Slot)
        {
            return Reject("packet.projectile.owner_mismatch");
        }

        if (projectile.Key.Index > MaximumProjectileIndex)
        {
            return Reject("packet.projectile.index_out_of_range");
        }

        if (!float.IsFinite(projectile.PositionX) ||
            !float.IsFinite(projectile.PositionY))
        {
            return Reject("packet.projectile.non_finite_position");
        }

        if (!float.IsFinite(projectile.VelocityX) ||
            !float.IsFinite(projectile.VelocityY))
        {
            return Reject("packet.projectile.non_finite_velocity");
        }

        return Continue();
    }

    private static PacketProcessingDecision Continue() =>
        new(PacketDecisionKind.Continue);

    private static PacketProcessingDecision Reject(string reason) =>
        new(PacketDecisionKind.Reject, reason);
}
