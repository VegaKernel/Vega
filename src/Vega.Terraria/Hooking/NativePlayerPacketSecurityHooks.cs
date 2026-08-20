using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using OTAPI;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Owns Vega-native inbound packet security and gameplay policy checks.
/// </summary>
internal sealed class NativePlayerPacketSecurityHooks : IDisposable
{
    private readonly IPacketProcessor<PacketView> packetProcessor;
    private bool attached;

    public NativePlayerPacketSecurityHooks(IPacketProcessor<PacketView> packetProcessor)
    {
        this.packetProcessor = packetProcessor ??
            throw new ArgumentNullException(nameof(packetProcessor));
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
        if (args.Result == HookResult.Cancel || !IsProcessedPacket(args.PacketId))
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
            packetProcessor,
            DateTimeOffset.UtcNow);

        if (decision.Kind is PacketDecisionKind.Handled or PacketDecisionKind.Reject)
        {
            args.Result = HookResult.Cancel;
        }
    }

    private static bool IsProcessedPacket(byte packetId) =>
        packetId is
            (byte)PacketTypes.PlayerHp or
            (byte)PacketTypes.PlayerMana or
            (byte)PacketTypes.PlayerTeam or
            (byte)PacketTypes.TogglePvp or
            (byte)PacketTypes.SpectatePlayer or
            (byte)PacketTypes.SyncExtraValue or
            (byte)PacketTypes.ProjectileNew or
            (byte)PacketTypes.PlayerHurtV2 or
            (byte)PacketTypes.PlayerDeathV2;
}
