using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using OTAPI;
using TShockNG.Contracts.Networking;
using TShockNG.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Owns Vega-native inbound packet security checks that run before the legacy TSAPI hook bridge.
/// </summary>
internal sealed class NativePlayerPacketSecurityHooks : IDisposable
{
    private readonly IPacketProcessor<PacketView> playerHpProcessor;
    private bool attached;

    public NativePlayerPacketSecurityHooks(IPacketProcessor<PacketView> playerHpProcessor)
    {
        this.playerHpProcessor = playerHpProcessor ?? throw new ArgumentNullException(nameof(playerHpProcessor));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData += OnReceiveData;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData -= OnReceiveData;
        attached = false;
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel || args.PacketId != (byte)PacketTypes.PlayerHp)
        {
            return;
        }

        var slot = args.Instance.whoAmI;
        var decision = MultiplicityPacketProcessing.ProcessInboundPayload(
            args.PacketId,
            args.Instance.readBuffer,
            args.ReadOffset,
            args.Length,
            new ConnectionId(slot),
            new PlayerId(slot),
            playerHpProcessor,
            DateTimeOffset.UtcNow);

        if (decision.Kind is PacketDecisionKind.Handled or PacketDecisionKind.Reject)
        {
            args.Result = HookResult.Cancel;
        }
    }
}
