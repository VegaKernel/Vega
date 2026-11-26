using Multiplicity.Packets;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Threading;
using Vega.Terraria.Players;

namespace Vega.Terraria.Networking;

public sealed class TerrariaSandboxPlayerTransfer(
    IGameThreadDispatcher dispatcher,
    TerrariaConnectionMetadataRegistry connections,
    ISandboxTransferAdmissionService admissions,
    ISandboxSscStore? sscStore = null,
    ISandboxSscCharacterAdapter? characterAdapter = null,
    TerrariaPlayerBoundary? players = null) : ISandboxPlayerTransferBoundary
{
    public ValueTask<SandboxPlayerTransferResult> TransferAsync(
        PlayerId playerId,
        SandboxEndpoint endpoint,
        SandboxTransferTicket ticket,
        CancellationToken cancellationToken = default)
    {
        if (players is null || !players.TryGet(playerId, out var player) || player is null)
            return ValueTask.FromResult(new SandboxPlayerTransferResult(false, "sandbox.transfer.player_not_found"));
        return TransferAsync(player, endpoint, ticket, cancellationToken);
    }

    public async ValueTask<SandboxPlayerTransferResult> TransferAsync(PlayerSnapshot player, SandboxEndpoint endpoint, SandboxTransferTicket ticket, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.cancelled");
        }

        if (ticket.SscLeaseId is Guid leaseId)
        {
            if (sscStore is null || characterAdapter is null)
            {
                return new SandboxPlayerTransferResult(false, "sandbox.ssc.store_unavailable");
            }

            SandboxSscCharacterCaptureResult captured = await characterAdapter.CaptureAsync(
                player,
                cancellationToken).ConfigureAwait(false);
            if (!captured.Succeeded || captured.Payload is null)
            {
                return new SandboxPlayerTransferResult(
                    false,
                    captured.ErrorCode ?? "sandbox.ssc.capture_failed");
            }

            SandboxSscTransferPreparation staged = sscStore.StageTransferState(
                leaseId,
                ticket.AccountId,
                captured.Payload,
                DateTimeOffset.UtcNow);
            if (!staged.Accepted)
            {
                return new SandboxPlayerTransferResult(
                    false,
                    staged.ErrorCode ?? "sandbox.ssc.stage_failed");
            }
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.dispatcher_unavailable");
        }

        if (dispatcher.IsGameThread)
        {
            return Transfer(player, endpoint, ticket);
        }

        var completion = new TaskCompletionSource<SandboxPlayerTransferResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("sandbox.transfer", () => completion.TrySetResult(
                cancellationToken.IsCancellationRequested
                    ? new SandboxPlayerTransferResult(false, "sandbox.transfer.cancelled")
                    : Transfer(player, endpoint, ticket))))
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.dispatcher_unavailable");
        }

        return await completion.Task.ConfigureAwait(false);
    }

    private SandboxPlayerTransferResult Transfer(
        PlayerSnapshot player,
        SandboxEndpoint endpoint,
        SandboxTransferTicket ticket)
    {
        PlayerId playerId = player.Id;
        if (players?.TryGetSessionPlayer(player, out _) != true || (uint)playerId.Slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[playerId.Slot] is not { IsActive: true } client)
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.player_not_found");
        }

        string? transport = client.Socket.GetRemoteAddress().GetIdentifier();
        ConnectionMetadataSnapshot metadata = connections.Snapshot(
            playerId.Slot,
            transport,
            TerrariaConnectionState.FullyConnected);
        if (metadata.ForwardingProvenance != ConnectionForwardingProvenance.TrustedForwarder ||
            string.IsNullOrWhiteSpace(metadata.EffectiveEndpoint) ||
            string.IsNullOrWhiteSpace(metadata.ClientUuid))
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.dimensions_required");
        }

        SandboxTransferAdmissionResult admission = admissions.Register(
            ticket,
            metadata.EffectiveEndpoint,
            metadata.ClientUuid,
            DateTimeOffset.UtcNow);
        if (!admission.Accepted)
        {
            return new SandboxPlayerTransferResult(
                false,
                admission.ErrorCode ?? "sandbox.transfer.admission_failed");
        }

        byte[] data = DimensionsTransferPacket.Create(endpoint);
        client.Socket.AsyncSend(data, 0, data.Length, client.ServerWriteCallBack);
        return new SandboxPlayerTransferResult(true);
    }
}

public static class DimensionsTransferPacket
{
    public static byte[] Create(SandboxEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is < 1 or > 65535)
        {
            throw new ArgumentException("Dimensions transfer endpoint is invalid.", nameof(endpoint));
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            stream.Position = 2;
            writer.Write((byte)PacketTypes.Placeholder);
            writer.Write((short)3);
            writer.Write(endpoint.Host);
            writer.Write(checked((ushort)endpoint.Port));
            long length = stream.Position;
            stream.Position = 0;
            writer.Write(checked((short)length));
            return stream.ToArray();
        }
    }
}
