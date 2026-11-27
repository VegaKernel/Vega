using System.Diagnostics;
using Microsoft.Xna.Framework;
using Terraria.Net.Sockets;
using TerrariaRuntime;

namespace Vega.Terraria.Hooking;

internal sealed class NativeMovementInterestHooks(int updatesPerSecond, Func<bool> isGameThread) : IDisposable
{
    private const int Slots = 256;
    private const int MovementPacket = global::Terraria.ID.MessageID.PlayerControls;
    private const int FullyConnected = (int)Networking.TerrariaConnectionState.FullyConnected;
    private const int NearRadius = 180 * 16;
    private const int DistantRadius = 220 * 16;
    private readonly long interval = Stopwatch.Frequency / updatesPerSecond;
    private readonly int[] generations = new int[Slots];
    private readonly MovementSample[]?[] samples = new MovementSample[Slots][];
    private Hooks.NetMessage.SendDataEventArgs? relay;
    private long now;

    public void Attach()
    {
        Hooks.NetMessage.SendData += OnSendData;
        Hooks.NetMessage.SendBytesReadOnly += OnSendBytes;
        Hooks.Netplay.ServerConnectionActivated += OnActivated;
    }

    public void Dispose()
    {
        Hooks.NetMessage.SendData -= OnSendData;
        Hooks.NetMessage.SendBytesReadOnly -= OnSendBytes;
        Hooks.Netplay.ServerConnectionActivated -= OnActivated;
    }

    private void OnActivated(int slot, ISocket socket)
    {
        if ((uint)slot < Slots) Interlocked.Increment(ref generations[slot]);
    }

    private void OnSendData(object? sender, Hooks.NetMessage.SendDataEventArgs args)
    {
        if (!isGameThread()) return;
        relay = null;
        if (args.Event != HookEvent.Before || args.Result == HookResult.Cancel || args.MsgType != MovementPacket ||
            args.RemoteClient >= 0 || args.IgnoreClient != args.Number || (uint)args.Number >= Slots ||
            global::Terraria.Netplay.Clients[args.Number]?.State != FullyConnected) return;
        relay = args;
        now = Stopwatch.GetTimestamp();
    }

    private void OnSendBytes(ref Hooks.NetMessage.ReadOnlySendBytesContext args)
    {
        if (relay is not { } current || !isGameThread()) return;

        int source = current.Number, target = args.RemoteClient;
        if ((uint)source >= Slots || (uint)target >= Slots) return;
        if (global::Terraria.Main.player[source] is not { active: true } origin ||
            global::Terraria.Main.player[target] is not { active: true } viewer) return;
        var row = samples[source];
        bool near = viewer.netCameraTarget.HasValue || Vector2.DistanceSquared(origin.position, viewer.position) < NearRadius * NearRadius;
        // A nearby recipient with no distant history needs neither packet inspection nor a state change.
        if (near && (row is null || !row[target].Distant)) return;
        FilterTrackedMovement(ref args, current, origin, viewer, near);
    }

    private void FilterTrackedMovement(ref Hooks.NetMessage.ReadOnlySendBytesContext args,
        Hooks.NetMessage.SendDataEventArgs current, global::Terraria.Player origin, global::Terraria.Player viewer,
        bool near)
    {
        if (args.Cancelled || current.Result == HookResult.Cancel || args.Size < 17) return;
        int source = current.Number, target = args.RemoteClient;
        var data = args.Data;
        if (data[2] != MovementPacket || data[3] != source ||
            global::Terraria.Netplay.Clients[target] is not { State: FullyConnected } remote ||
            !ReferenceEquals(args.Socket, remote.Socket)) return;
        if (near)
        {
            samples[source]![target].MarkNear();
            return;
        }
        if (!ReferenceEquals(args.Callback.Target, remote) ||
            args.Callback.Method.Name != nameof(global::Terraria.RemoteClient.ServerWriteCallBack)) return;

        var row = samples[source] ??= new MovementSample[Slots];
        ReadOnlySpan<byte> payload = data.Slice(4, 5);
        long state = (uint)((payload[0] & 0x20) | ((payload[1] & 0xfb) << 8) | (payload[2] << 16)) |
            ((long)payload[3] << 24) | ((long)payload[4] << 32);
        bool special = (payload[2] & 0x40) != 0 || (payload[3] & 0x38) != 0;
        if (!row[target].ShouldSend(origin.position, viewer.position, state, special,
                Volatile.Read(ref generations[source]), Volatile.Read(ref generations[target]), now, interval))
        {
            args.Cancel();
            args.Callback(args.State);
        }
    }

    internal struct MovementSample
    {
        private Vector2 lastPosition;
        private long lastStamp;
        private long lastState;
        private int sourceGeneration;
        private int targetGeneration;
        public bool Distant { get; private set; }

        public void MarkNear() => Distant = false;

        public bool ShouldSend(Vector2 origin, Vector2 viewer, long state, bool special,
            int sourceSession, int targetSession, long stamp, long intervalTicks)
        {
            bool sameSession = lastStamp != 0 && sourceSession == sourceGeneration && targetSession == targetGeneration;
            int radius = sameSession && Distant ? NearRadius : DistantRadius;
            bool far = Vector2.DistanceSquared(origin, viewer) >= radius * radius;
            bool send = !far || !sameSession || !Distant || special || state != lastState ||
                Vector2.DistanceSquared(lastPosition, origin) >= 640 * 640 || stamp - lastStamp >= intervalTicks;
            if (!send) return false;
            lastPosition = origin;
            lastStamp = stamp;
            lastState = state;
            sourceGeneration = sourceSession;
            targetGeneration = targetSession;
            Distant = far;
            return true;
        }
    }
}
