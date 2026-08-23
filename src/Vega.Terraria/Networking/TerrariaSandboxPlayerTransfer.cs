using Multiplicity.Packets;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Networking;

public sealed class TerrariaSandboxPlayerTransfer(
    IGameThreadDispatcher dispatcher,
    TerrariaConnectionMetadataRegistry connections) : ISandboxPlayerTransferBoundary
{
    public ValueTask<SandboxPlayerTransferResult> TransferAsync(
        PlayerId playerId,
        SandboxEndpoint endpoint,
        SandboxTransferTicket ticket,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new SandboxPlayerTransferResult(false, "sandbox.transfer.cancelled"));
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(new SandboxPlayerTransferResult(false, "sandbox.transfer.dispatcher_unavailable"));
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(Transfer(playerId, endpoint, ticket));
        }

        var completion = new TaskCompletionSource<SandboxPlayerTransferResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost("sandbox.transfer", () => completion.TrySetResult(
                cancellationToken.IsCancellationRequested
                    ? new SandboxPlayerTransferResult(false, "sandbox.transfer.cancelled")
                    : Transfer(playerId, endpoint, ticket))))
        {
            return ValueTask.FromResult(new SandboxPlayerTransferResult(false, "sandbox.transfer.dispatcher_unavailable"));
        }

        return new ValueTask<SandboxPlayerTransferResult>(completion.Task);
    }

    private SandboxPlayerTransferResult Transfer(
        PlayerId playerId,
        SandboxEndpoint endpoint,
        SandboxTransferTicket ticket)
    {
        if ((uint)playerId.Slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[playerId.Slot] is not { IsActive: true } client)
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.player_not_found");
        }

        string? transport = client.Socket.GetRemoteAddress().GetIdentifier();
        ConnectionMetadataSnapshot metadata = connections.Snapshot(
            playerId.Slot,
            transport,
            TerrariaConnectionState.FullyConnected);
        if (metadata.ForwardingProvenance != ConnectionForwardingProvenance.TrustedForwarder)
        {
            return new SandboxPlayerTransferResult(false, "sandbox.transfer.dimensions_required");
        }

        byte[] data = DimensionsTransferPacket.Create(endpoint);
        client.Socket.AsyncSend(data, 0, data.Length, client.ServerWriteCallBack);
        GC.KeepAlive(ticket);
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
