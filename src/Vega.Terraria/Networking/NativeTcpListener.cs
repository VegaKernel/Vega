using System.Net;
using System.Net.Sockets;
using Terraria.Net;
using Terraria.Net.Sockets;

namespace Vega.Terraria.Networking;

/// <summary>Dedicated listener adapter; accepted sockets still enter vanilla through its accepted callback.</summary>
internal sealed class NativeTcpListener(Func<IPEndPoint> bindEndpoint, Func<Socket, NativeTcpConnection?> createConnection) : ISocket
{
    private readonly object gate = new();
    private TcpListener? listener;
    private CancellationTokenSource? stopping;
    private Task? acceptLoop;

    public IPEndPoint? LocalEndPoint
    {
        get { lock (gate) return listener?.LocalEndpoint as IPEndPoint; }
    }

    public Task Completion
    {
        get { lock (gate) return acceptLoop ?? Task.CompletedTask; }
    }

    public bool StartListening(SocketConnectionAccepted callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (gate)
        {
            if (listener is not null) return stopping?.IsCancellationRequested == false;
            if (acceptLoop is { IsCompleted: false }) return false;
            TcpListener candidate = new(bindEndpoint());
            try { candidate.Start(); }
            catch (SocketException)
            {
                candidate.Stop();
                return false;
            }
            listener = candidate;
            var cancellation = new CancellationTokenSource();
            stopping = cancellation;
            acceptLoop = Task.Run(() => AcceptLoopAsync(candidate, callback, cancellation));
            return true;
        }
    }

    public void StopListening()
    {
        lock (gate)
        {
            if (listener is null || stopping is null || stopping.IsCancellationRequested) return;
            stopping.Cancel();
            listener.Stop();
        }
    }

    public void Close() => StopListening();
    public bool IsConnected() => false;
    public bool IsDataAvailable() => false;
    public RemoteAddress GetRemoteAddress()
    {
        IPEndPoint endpoint = LocalEndPoint ?? bindEndpoint();
        return new TcpAddress(endpoint.Address, endpoint.Port);
    }
    public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException("A dedicated listener cannot connect.");
    public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null) =>
        throw new NotSupportedException("A dedicated listener cannot send.");
    public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) =>
        throw new NotSupportedException("A dedicated listener cannot receive.");

    private async Task AcceptLoopAsync(TcpListener owner, SocketConnectionAccepted callback, CancellationTokenSource cancellation)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Socket accepted = await owner.AcceptSocketAsync(token).ConfigureAwait(false);
                NativeTcpConnection? connection = null;
                try
                {
                    if (token.IsCancellationRequested) continue;
                    connection = createConnection(accepted);
                    if (connection is null) continue;
                    bool publish;
                    lock (gate) publish = !token.IsCancellationRequested;
                    if (!publish)
                    {
                        connection.Close();
                        continue;
                    }
                    // The claim above is the linearization point relative to StopListening. Do not hold a
                    // listener lock across an external admission callback. StopAsync joins this loop.
                    callback(connection);
                }
                catch (Exception)
                {
                    connection?.Close();
                }
                finally
                {
                    if (connection is null) accepted.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
        finally
        {
            lock (gate)
            {
                owner.Stop();
                if (ReferenceEquals(listener, owner))
                {
                    listener = null;
                    stopping = null;
                }
                cancellation.Dispose();
            }
        }
    }
}
