using System.Diagnostics;
using TerrariaRuntime;
using Vega.Contracts.Diagnostics;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Hooking;

internal sealed class NativeNetworkMetricsHooks : IDisposable
{
    private static readonly DiagnosticListener Diagnostics = new("Vega.Network");
    private const int FramesPerSlotPerPass = 32;
    private const int PendingConnectionLimit = 128;
    private readonly INetworkMetricsRecorder metrics;
    private bool attached;
    private int previousPendingConnectionLimit;
    private Func<int, int>? previousFrameBudget;

    public NativeNetworkMetricsHooks(INetworkMetricsRecorder metrics)
    {
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        previousPendingConnectionLimit = Hooks.Netplay.ServerPendingConnectionLimit;
        previousFrameBudget = Hooks.NetMessage.ServerFrameBudget;
        Hooks.Netplay.ServerPendingConnectionLimit = PendingConnectionLimit;
        Hooks.NetMessage.ServerFrameBudget = GetFrameBudget;
        Hooks.Main.DedicatedTickCompleted += OnTickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted += OnSaveCompleted;
        Hooks.MessageBuffer.GetData += OnReceiveData;
        Hooks.NetMessage.SendBytes += OnSendBytes;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.Netplay.ServerPendingConnectionLimit = previousPendingConnectionLimit;
        Hooks.NetMessage.ServerFrameBudget = previousFrameBudget;
        Hooks.Main.DedicatedTickCompleted -= OnTickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted -= OnSaveCompleted;
        Hooks.MessageBuffer.GetData -= OnReceiveData;
        Hooks.NetMessage.SendBytes -= OnSendBytes;
        attached = false;
    }

    private static int GetFrameBudget(int _) => FramesPerSlotPerPass;

    private static void OnTickCompleted(TimeSpan duration)
    {
        if (Diagnostics.IsEnabled("World.TickCompleted"))
            Diagnostics.Write("World.TickCompleted", duration);
    }

    private static void OnSaveCompleted(bool success, Exception? error)
    {
        if (Diagnostics.IsEnabled("World.SaveCompleted"))
            Diagnostics.Write("World.SaveCompleted", (success, error));
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        metrics.RecordInbound(args.Length);
        if (Diagnostics.IsEnabled("Packet.Inbound"))
        {
            Diagnostics.Write("Packet.Inbound", new NetworkPacketDiagnosticEvent(
                "Inbound",
                args.PacketId,
                args.Length,
                args.Instance.whoAmI,
                DateTimeOffset.UtcNow));
        }
    }

    private void OnSendBytes(object? sender, Hooks.NetMessage.SendBytesEventArgs args)
    {
        metrics.RecordOutbound(args.Size);
        if (Diagnostics.IsEnabled("Packet.Outbound"))
        {
            Diagnostics.Write("Packet.Outbound", new NetworkPacketDiagnosticEvent(
                "Outbound",
                null,
                args.Size,
                null,
                DateTimeOffset.UtcNow));
        }
    }
}
