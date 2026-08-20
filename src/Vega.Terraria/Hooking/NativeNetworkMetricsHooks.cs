using OTAPI;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Hooking;

internal sealed class NativeNetworkMetricsHooks : IDisposable
{
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

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args) =>
        metrics.RecordInbound(args.Length);

    private void OnSendBytes(object? sender, Hooks.NetMessage.SendBytesEventArgs args) =>
        metrics.RecordOutbound(args.Size);
}
