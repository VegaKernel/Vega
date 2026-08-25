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
    private bool attached;

    public NativePlayerPacketSecurityHooks(
        IPacketProcessor<PacketView> packetProcessor,
        TerrariaConnectionMetadataRegistry connectionMetadata,
        IOperationsLogger logger,
        INetworkMetricsRecorder metrics,
        ISecuritySignalRecorder securitySignals)
    {
        this.packetProcessor = packetProcessor ??
            throw new ArgumentNullException(nameof(packetProcessor));
        this.connectionMetadata = connectionMetadata ??
            throw new ArgumentNullException(nameof(connectionMetadata));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        this.securitySignals = securitySignals ??
            throw new ArgumentNullException(nameof(securitySignals));
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
        ReadOnlySpan<byte> payload;
        try
        {
            payload = args.Instance.readBuffer.AsSpan(args.ReadOffset, args.Length);
            ConnectionMetadataObservation metadata = connectionMetadata.ObserveInboundDetailed(
                args.PacketId,
                payload,
                slot,
                connectionState,
                TryGetRemoteEndpoint(args));
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
                PacketProcessingDecision.Reject("connection_metadata.parse_failed"));
            return;
        }

        PacketProcessingDecision ingressDecision;
        try
        {
            ingressDecision = TerrariaInboundPacketGuard.Validate(
                args.PacketId,
                connectionState,
                slot,
                payload);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "packet-security",
                $"Inbound packet guard failed for packet {args.PacketId}, slot {slot}; packet rejected.",
                ex);
            RecordRejection(
                args,
                slot,
                PacketProcessingDecision.Violation("packet.ingress.guard_exception"));
            return;
        }

        if (ingressDecision.Kind != PacketDecisionKind.Allow)
        {
            RecordRejection(args, slot, ingressDecision);
            return;
        }

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
            logger.Log(
                OperationsLogLevel.Error,
                "packet-security",
                $"Inbound packet security failed for packet {args.PacketId}, slot {slot}, state {connectionState}; packet rejected fail-closed.",
                ex);
            RecordRejection(
                args,
                slot,
                PacketProcessingDecision.Violation("packet.security.processor_exception"));
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

        if (args.PacketId == (byte)PacketTypes.NpcStrike &&
            connectionState >= TerrariaConnectionState.FullyConnected)
        {
            TryAcknowledgeNpcStrike(slot);
        }

        args.Result = HookResult.Cancel;

        if (ShouldTerminateConnection(connectionState, decision.Kind))
        {
            TerminateConnection(slot, decision.Reason);
        }
    }

    private void TryAcknowledgeNpcStrike(int slot)
    {
        try
        {
            global::Terraria.NetMessage.TrySendData((int)PacketTypes.DamageNPCAck, slot);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "packet-security",
                $"Failed to acknowledge rejected NPC strike for slot {slot}.",
                ex);
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
                global::Terraria.Localization.NetworkText.FromLiteral("Invalid connection handshake."));
            logger.Log(
                OperationsLogLevel.Debug,
                "packet-security",
                $"Rejected pre-login connection from slot {slot} ({reason ?? "no-reason"}).");
        }
        catch (Exception ex)
        {
            client.PendingTermination = true;
            client.PendingTerminationApproved = true;
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
        connectionState >= TerrariaConnectionState.FullyConnected &&
        PacketPipelineClassification.Get(packetId).SecurityProcessed &&
        PacketPipelineClassification.Get(packetId).Ingress == PacketIngressDisposition.GameplayValidated;

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
}
