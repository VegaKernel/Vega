using TerrariaRuntime;

namespace Vega.Terraria.Hooking;

internal static class NativeInboundPacketExtensions
{
    public static int GetPayloadLength(this Hooks.MessageBuffer.GetDataEventArgs args) =>
        args.Length - (args.ReadOffset - args.Start);
}
