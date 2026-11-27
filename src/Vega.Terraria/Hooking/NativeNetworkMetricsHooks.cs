using System.Diagnostics;
using TerrariaRuntime;
using Vega.Contracts.Diagnostics;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Hooking;

internal sealed class NativeNetworkMetricsHooks : IDisposable
{
    private static readonly DiagnosticListener Diagnostics = new("Vega.Network");
    private readonly INetworkMetricsRecorder metrics;
    private bool attached;

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

        Hooks.Main.DedicatedTickCompleted += OnTickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted += OnSaveCompleted;
        Hooks.MessageBuffer.GetData += OnReceiveData;
        Hooks.NetMessage.SendBytesReadOnly += OnSendBytes;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.Main.DedicatedTickCompleted -= OnTickCompleted;
        Hooks.Main.DedicatedWorldSaveCompleted -= OnSaveCompleted;
        Hooks.MessageBuffer.GetData -= OnReceiveData;
        Hooks.NetMessage.SendBytesReadOnly -= OnSendBytes;
        attached = false;
    }

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

    private void OnSendBytes(ref Hooks.NetMessage.ReadOnlySendBytesContext args)
    {
        if (args.Cancelled) return;
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
