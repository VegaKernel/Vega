using Vega.Contracts.Networking;

namespace Vega.PluginSdk;

public interface IPluginPackets : IInboundPacketRateReader
{
    /// <summary>Called synchronously for inbound frames, including pre-login traffic.
    /// Return false to drop this frame. Registration is removed on plugin retirement.</summary>
    void AllowInbound(Func<InboundPacketRate, bool> allow);
}
