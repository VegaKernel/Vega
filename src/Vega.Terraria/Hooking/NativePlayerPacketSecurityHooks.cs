using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using OTAPI;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Owns Vega-native inbound packet security and gameplay policy checks.
/// </summary>
internal sealed class NativePlayerPacketSecurityHooks : IDisposable
{
    private readonly IPacketProcessor<PacketView> packetProcessor;
    private readonly IOperationsLogger logger;
    private bool attached;

    public NativePlayerPacketSecurityHooks(
        IPacketProcessor<PacketView> packetProcessor,
        IOperationsLogger logger)
    {
        this.packetProcessor = packetProcessor ??
            throw new ArgumentNullException(nameof(packetProcessor));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        if (args.Result == HookResult.Cancel)
        {
            return;
        }

        int slot = args.Instance.whoAmI;
        TerrariaConnectionState connectionState = GetConnectionState(slot);
        if (!ShouldApplySecurity(connectionState, args.PacketId))
        {
            return;
        }

        try
        {
            PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
                args.PacketId,
                args.Instance.readBuffer,
                args.ReadOffset,
                args.Length,
                new ConnectionId(slot),
                new PlayerId(slot),
                packetProcessor,
                DateTimeOffset.UtcNow);

            if (ShouldCancelVanillaProcessing(decision.Kind))
            {
                args.Result = HookResult.Cancel;
            }
        }
        catch (Exception ex)
        {
            // Packet policy is an additional Vega guard, not the Terraria connection state machine.
            // A policy bug must not strand a client or tear down MessageBuffer.GetData.
            logger.Log(
                OperationsLogLevel.Error,
                "packet-security",
                $"Inbound packet security failed for packet {args.PacketId}, slot {slot}, state {connectionState}; vanilla processing continues.",
                ex);
        }
    }

    internal static bool ShouldApplySecurity(TerrariaConnectionState connectionState, byte packetId) =>
        connectionState >= TerrariaConnectionState.FullyConnected && IsProcessedPacket(packetId);

    internal static bool ShouldCancelVanillaProcessing(PacketDecisionKind kind) =>
        kind is PacketDecisionKind.Handled or PacketDecisionKind.Reject or PacketDecisionKind.Violation;

    private static TerrariaConnectionState GetConnectionState(int slot)
    {
        if ((uint)slot >= (uint)global::Terraria.Netplay.Clients.Length)
        {
            return TerrariaConnectionState.PasswordChallenge;
        }

        int state = global::Terraria.Netplay.Clients[slot]?.State ??
            (int)TerrariaConnectionState.PasswordChallenge;
        return (TerrariaConnectionState)state;
    }

    private static bool IsProcessedPacket(byte packetId) =>
        packetId is
            (byte)PacketTypes.PlayerHp or
            (byte)PacketTypes.PlayerMana or
            (byte)PacketTypes.PlayerTeam or
            (byte)PacketTypes.TeamChangeFromUI or
            (byte)PacketTypes.TogglePvp or
            (byte)PacketTypes.SpectatePlayer or
            (byte)PacketTypes.SyncExtraValue or
            (byte)PacketTypes.ItemDrop or
            (byte)PacketTypes.UpdateItemDrop or
            (byte)PacketTypes.ProjectileNew or
            (byte)PacketTypes.ProjectileDestroy or
            (byte)PacketTypes.PlayerHurtV2 or
            (byte)PacketTypes.PlayerDeathV2;
}
