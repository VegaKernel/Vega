using System.Diagnostics;
using TerrariaRuntime;
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

        Hooks.MessageBuffer.GetData -= OnReceiveData;
        Hooks.NetMessage.SendBytes -= OnSendBytes;
        attached = false;
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        metrics.RecordInbound(args.Length);
        if (Diagnostics.IsEnabled("Packet.Inbound"))
        {
            Diagnostics.Write("Packet.Inbound", new
            {
                PacketId = args.PacketId,
                Size = args.Length,
                Slot = args.Instance.whoAmI,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
    }

    private void OnSendBytes(object? sender, Hooks.NetMessage.SendBytesEventArgs args)
    {
        metrics.RecordOutbound(args.Size);
        if (Diagnostics.IsEnabled("Packet.Outbound"))
        {
            Diagnostics.Write("Packet.Outbound", new
            {
                Size = args.Size,
                Timestamp = DateTimeOffset.UtcNow
            });
        }
    }
}
