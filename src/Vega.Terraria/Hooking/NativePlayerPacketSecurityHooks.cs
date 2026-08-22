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
        try
        {
            PacketProcessingDecision metadataDecision = connectionMetadata.ObserveInbound(
                args.PacketId,
                args.Instance.readBuffer.AsSpan(args.ReadOffset, args.Length),
                slot,
                connectionState);
            if (metadataDecision.Kind != PacketDecisionKind.Allow)
            {
                RecordRejection(args, slot, metadataDecision);
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
            args.Result = HookResult.Cancel;
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
        metrics.RecordSecurityDecision(decision);
        ConnectionMetadataSnapshot connection = connectionMetadata.Snapshot(
            slot,
            TryGetRemoteEndpoint(args),
            GetConnectionState(slot));
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
            (byte)PacketTypes.PlayerUpdate or
            (byte)PacketTypes.PlayerHp or
            (byte)PacketTypes.PlayerMana or
            (byte)PacketTypes.PlayerTeam or
            (byte)PacketTypes.TeamChangeFromUI or
            (byte)PacketTypes.TogglePvp or
            (byte)PacketTypes.SpectatePlayer or
            (byte)PacketTypes.PlayerTeleportPortal or
            (byte)PacketTypes.SyncExtraValue or
            (byte)PacketTypes.ItemDrop or
            (byte)PacketTypes.UpdateItemDrop or
            (byte)PacketTypes.SyncItemsWithShimmerDeprecated or
            (byte)PacketTypes.SyncItemCannotBeTakenByEnemiesDeprecated or
            (byte)PacketTypes.SyncItemDespawn or
            (byte)PacketTypes.SyncItemPosition or
            (byte)PacketTypes.ProjectileNew or
            (byte)PacketTypes.ProjectileDestroy or
            (byte)PacketTypes.SyncProjectileTrackers or
            (byte)PacketTypes.PlayerHurtV2 or
            (byte)PacketTypes.PlayerDeathV2 or
            (byte)PacketTypes.Tile or
            (byte)PacketTypes.PlaceObject or
            (byte)PacketTypes.PlaceItemFrame or
            (byte)PacketTypes.ChestGetContents or
            (byte)PacketTypes.ChestItem or
            (byte)PacketTypes.ChestOpen or
            (byte)PacketTypes.PlaceChest or
            (byte)PacketTypes.SyncChestSize or
            (byte)PacketTypes.ChestName or
            (byte)PacketTypes.ChestUnlock or
            (byte)PacketTypes.SyncPlayerChestIndex or
            (byte)PacketTypes.ForceItemIntoNearestChest or
            (byte)PacketTypes.LiquidSet or
            (byte)PacketTypes.PlaceTileEntity or
            (byte)PacketTypes.GemLockToggle or
            (byte)PacketTypes.MassWireOperation or
            (byte)PacketTypes.FoodPlatterTryPlacing or
            (byte)PacketTypes.TEDeadCellsDisplayJar or
            (byte)PacketTypes.TileEntityDisplayDollItemSync or
            (byte)PacketTypes.RequestTileEntityInteraction or
            (byte)PacketTypes.TileEntityHatRackItemSync or
            (byte)PacketTypes.SyncTilePicking or
            (byte)PacketTypes.TELeashedEntityAnchorPlaceItem;
}
