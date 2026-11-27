using Vega.Contracts.Networking;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginPackets(HotModuleRegistrationScope owner, IInboundPacketRateReader? rates,
        IWorldGameplayEvents? events) : IPluginPackets
    {
        public InboundPacketRate? Read(ConnectionId connection, byte packetId) => rates?.Read(connection, packetId);

        public void AllowInbound(Func<InboundPacketRate, bool> allow)
        {
            ArgumentNullException.ThrowIfNull(allow);
            if (rates is null || events is null) throw new NotSupportedException("Inbound packet accounting is unavailable in this host.");
            owner.RegisterOwned(() => events.SubscribePolicy<InboundPacketRate>(packet =>
                owner.lifetime.IsCancellationRequested || owner.executionGuard.Allows("network.packet.inbound", allow, packet)));
        }
    }
}
