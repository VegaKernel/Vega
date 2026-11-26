using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Networking;

public sealed class SceneEntityInteractionAuthorityPolicy(ISceneVisibilityRuntime visibility, WorldId? ownedWorld = null)
    : IPacketProcessor<PacketView>
{
    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (context.Direction != PacketDirection.Inbound ||
            context.PlayerId is not { } playerId ||
            !visibility.HasAssignments ||
            !TryResolve(context.Packet, out SceneEntity entity))
        {
            return PacketProcessingDecision.Allow;
        }

        return visibility.CanObserve(ownedWorld ?? WorldId.Primary, playerId, entity)
            ? PacketProcessingDecision.Allow
            : PacketProcessingDecision.Reject("scene.hidden_entity_interaction");
    }

    private static bool TryResolve(PacketView packet, out SceneEntity entity)
    {
        switch (packet.PacketType)
        {
            case PacketTypes.NpcStrike:
                entity = new SceneEntity(SceneEntityKind.Npc, packet.AsNpcStrikeView().NpcSlot);
                return true;
            case PacketTypes.NpcItemStrike:
                entity = new SceneEntity(SceneEntityKind.Npc, packet.AsNpcItemStrikeView().NpcId);
                return true;
            case PacketTypes.ProjectileDestroy:
                entity = new SceneEntity(
                    SceneEntityKind.Projectile,
                    packet.AsProjectileDestroyView().Key.Index);
                return true;
            default:
                entity = default;
                return false;
        }
    }
}
