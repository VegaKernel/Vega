using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;

namespace Vega.Core.Networking;

/// <summary>
/// Rejects client-authored projectile destruction for a foreign or invalid protocol-325 projectile key.
/// </summary>
public sealed class ProjectileDestroySanityPolicy : IPacketProcessor<PacketView>
{
    private const ushort MaximumProjectileIndex = 1000;

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.Packet.PacketType != PacketTypes.ProjectileDestroy)
        {
            return Continue();
        }

        var projectile = new ProjectileDestroyView(context.Packet);
        if (projectile.Key.Spawner != context.ConnectionId.Slot)
        {
            return Violation("packet.projectile_destroy.owner_mismatch");
        }

        return projectile.Key.Index > MaximumProjectileIndex
            ? Violation("packet.projectile_destroy.index_out_of_range")
            : Continue();
    }

    private static PacketProcessingDecision Continue() =>
        new(PacketDecisionKind.Continue);

    private static PacketProcessingDecision Violation(string reason) =>
        new(PacketDecisionKind.Violation, reason);
}
