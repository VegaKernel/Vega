using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using TerrariaRuntime;
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
    private readonly TerrariaConnectionMetadataRegistry connectionMetadata;
    private readonly IOperationsLogger logger;
    private readonly INetworkMetricsRecorder metrics;
    private readonly ISecuritySignalRecorder securitySignals;
    private readonly IConnectionAdmissionPolicy? admission;
    private readonly IPacketProcessor<PacketView>? joinInventoryPolicy;
    private bool attached;

    public NativePlayerPacketSecurityHooks(
        IPacketProcessor<PacketView> packetProcessor,
        TerrariaConnectionMetadataRegistry connectionMetadata,
        IOperationsLogger logger,
        INetworkMetricsRecorder metrics,
        ISecuritySignalRecorder securitySignals,
        IConnectionAdmissionPolicy? admission = null,
        IPacketProcessor<PacketView>? joinInventoryPolicy = null)
    {
        this.packetProcessor = packetProcessor ??
            throw new ArgumentNullException(nameof(packetProcessor));
        this.connectionMetadata = connectionMetadata ??
            throw new ArgumentNullException(nameof(connectionMetadata));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        this.securitySignals = securitySignals ??
            throw new ArgumentNullException(nameof(securitySignals));
        this.admission = admission;
        this.joinInventoryPolicy = joinInventoryPolicy;
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
        int payloadLength = args.Length - (args.ReadOffset - args.Start);
        try
        {
            ConnectionMetadataObservation metadata = connectionMetadata.ObserveInboundDetailed(
                args.PacketId,
                args.Instance.readBuffer.AsSpan(args.ReadOffset, payloadLength),
                slot,
                connectionState,
                // The registry only needs a transport endpoint when interpreting
                // a forwarding-metadata packet. Ordinary gameplay never formats
                // the socket address; admission/rejection still resolve it explicitly.
                args.PacketId == (byte)PacketTypes.Placeholder ? TryGetRemoteEndpoint(args) : null);
            if (metadata.Decision.Kind != PacketDecisionKind.Allow)
            {
                RecordRejection(args, slot, metadata.Decision);
                return;
            }

            if (metadata.Consumed)
            {
                args.Result = HookResult.Cancel;
                return;
            }

            if (ShouldApplyAdmission(connectionState, args.PacketId))
            {
                PacketProcessingDecision decision = connectionMetadata.ValidateWorldAdmission(slot);
                if (decision.Kind == PacketDecisionKind.Allow && admission is not null)
                    decision = admission.Evaluate(connectionMetadata.Snapshot(slot, TryGetRemoteEndpoint(args), connectionState));
                if (decision.Kind != PacketDecisionKind.Allow)
                {
                    RecordRejection(args, slot, decision);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "connection-metadata",
                $"Connection metadata parsing failed for packet {args.PacketId}, slot {slot}; packet rejected.",
                ex);
            RecordRejection(
                args,
                slot,
                new PacketProcessingDecision(
                    PacketDecisionKind.Reject,
                    "connection_metadata.parse_failed"));
            return;
        }

        bool preWorldInventory = ShouldApplyPreWorldInventory(connectionState, args.PacketId);
        var processor = ShouldApplySecurity(connectionState, args.PacketId) ? packetProcessor
            : preWorldInventory ? joinInventoryPolicy : null;
        if (processor is null)
        {
            return;
        }

        try
        {
            PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
                args.PacketId,
                args.Instance.readBuffer,
                args.ReadOffset,
                payloadLength,
                new ConnectionId(slot),
                new PlayerId(slot),
                processor,
                DateTimeOffset.UtcNow);

            if (decision.Kind != PacketDecisionKind.Allow)
            {
                RecordRejection(args, slot, decision);
            }
            else
            {
                metrics.RecordSecurityDecision(decision);
            }
        }
        catch (Exception ex)
        {
            if (preWorldInventory)
            {
                logger.Log(OperationsLogLevel.Error, "packet-security", "Initial character synchronization could not be validated.", ex);
                RecordRejection(args, slot, PacketProcessingDecision.Reject("packet.inventory.validation_failed"));
                return;
            }
            // Packet policy is an additional Vega guard, not the Terraria connection state machine.
            // A policy bug must not strand a client or tear down MessageBuffer.GetData.
            logger.Log(
                OperationsLogLevel.Error,
                "packet-security",
                $"Inbound packet security failed for packet {args.PacketId}, slot {slot}, state {connectionState}; vanilla processing continues.",
                ex);
        }
    }

    private void RecordRejection(
        Hooks.MessageBuffer.GetDataEventArgs args,
        int slot,
        PacketProcessingDecision decision)
    {
        TerrariaConnectionState connectionState = GetConnectionState(slot);
        metrics.RecordSecurityDecision(decision);
        ConnectionMetadataSnapshot connection = connectionMetadata.Snapshot(
            slot,
            TryGetRemoteEndpoint(args),
            connectionState);
        securitySignals.Record(new SecuritySignal(
            DateTimeOffset.UtcNow,
            new ConnectionId(slot),
            args.PacketId,
            decision.Kind,
            decision.Reason ?? "packet.unspecified",
            connection.TransportEndpoint,
            connection.EffectiveEndpoint,
            connection.ForwardingProvenance));
        logger.Log(
            decision.Kind == PacketDecisionKind.Violation
                ? OperationsLogLevel.Warning
                : OperationsLogLevel.Debug,
            "packet-security",
            $"Inbound packet {args.PacketId} from slot {slot}: {decision.Kind} ({decision.Reason ?? "no-reason"}).");
        args.Result = HookResult.Cancel;

        if (ShouldTerminateConnection(connectionState, decision.Kind))
        {
            TerminateConnection(slot, decision.Reason);
        }
    }

    private void TerminateConnection(int slot, string? reason)
    {
        if ((uint)slot >= (uint)global::Terraria.Netplay.Clients.Length)
        {
            return;
        }

        global::Terraria.RemoteClient? client = global::Terraria.Netplay.Clients[slot];
        if (client is null || !client.IsConnected())
        {
            return;
        }

        try
        {
            global::Terraria.NetMessage.BootPlayer(
                slot,
                global::Terraria.Localization.NetworkText.FromLiteral(reason switch
                {
                    "admission.whitelist" => "This server requires a whitelisted IP address.",
                    "admission.whitelist_unavailable" => "The shared whitelist is temporarily unavailable. Contact the server operator.",
                    _ => "Invalid connection handshake."
                }));
            logger.Log(
                OperationsLogLevel.Debug,
                "packet-security",
                $"Rejected pre-login connection from slot {slot} ({reason ?? "no-reason"}).");
        }
        catch (Exception ex)
        {
            client.RequestTermination("Rejected connection handshake");
            logger.Log(
                OperationsLogLevel.Warning,
                "packet-security",
                $"Failed to send disconnect packet to rejected pre-login slot {slot}; connection marked for termination.",
                ex);
        }
    }

    private static string? TryGetRemoteEndpoint(Hooks.MessageBuffer.GetDataEventArgs args)
    {
        try
        {
            return global::Terraria.Netplay.Clients[args.Instance.whoAmI]?.Socket?
                .GetRemoteAddress()
                .GetIdentifier();
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool ShouldApplySecurity(TerrariaConnectionState connectionState, byte packetId) =>
        connectionState >= TerrariaConnectionState.FullyConnected && IsProcessedPacket(packetId);

    internal static bool ShouldApplyPreWorldInventory(TerrariaConnectionState connectionState, byte packetId) =>
        connectionState >= TerrariaConnectionState.ProtocolAccepted && connectionState < TerrariaConnectionState.FullyConnected &&
        (PacketTypes)packetId is PacketTypes.PlayerSlot or PacketTypes.SyncLoadout;

    internal static bool ShouldApplyAdmission(TerrariaConnectionState connectionState, byte packetId) =>
        connectionState < TerrariaConnectionState.FullyConnected && packetId == global::Terraria.ID.MessageID.RequestWorldData;

    internal static bool ShouldTerminateConnection(
        TerrariaConnectionState connectionState,
        PacketDecisionKind decisionKind) =>
        connectionState < TerrariaConnectionState.FullyConnected &&
        decisionKind is PacketDecisionKind.Reject or PacketDecisionKind.Violation;

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
        PacketPipelineClassification.Get(packetId).SecurityProcessed;
}
