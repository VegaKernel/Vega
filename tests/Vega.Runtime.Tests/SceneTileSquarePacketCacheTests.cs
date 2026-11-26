using NUnit.Framework;
using Terraria.Net;
using Terraria.Net.Sockets;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Terraria.Scenes;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class SceneTileSquarePacketCacheTests
{
    [Test]
    public void MaximumRendererChunkFitsNativePacketEvenWithEveryOptionalTileField()
    {
        int side = TerrariaSceneRenderSink.MaximumTileSquareSide;
        using var cache = new SceneTileSquarePacketCache(0, 0, side, side);
        bool[] frameImportant = [false, true];
        var denseTile = new SceneTile(1, 1, 18, 36, 255, 1, 1, 1, 1, SceneTileFlags.Active);
        byte[] packet = cache.Resolve(new PlayerId(1), (_, _, _) => denseTile, frameImportant);
        Assert.That(packet.Length, Is.EqualTo(10 + 15 * side * side));
        Assert.That(packet.Length, Is.LessThanOrEqualTo(short.MaxValue));
    }

    [Test]
    public void IdenticalCompositionsReuseExactNativePacketOnlyAfterIndependentResolution()
    {
        using var cache = new SceneTileSquarePacketCache(10, 20, 2, 1);
        var frameImportant = new bool[32]; frameImportant[21] = true;
        SceneTile tile = new(21, 5, 18, 36, 64, 2, 7, 8, 3,
            SceneTileFlags.Active | SceneTileFlags.WireRed | SceneTileFlags.WireBlue | SceneTileFlags.WireGreen |
            SceneTileFlags.WireYellow | SceneTileFlags.FullbrightBlock | SceneTileFlags.InvisibleWall);
        var calls = new List<PlayerId>();
        SceneTile Resolve(PlayerId player, int x, int y) { calls.Add(player); return x == 10 ? tile : SceneTile.Air; }
        var first = new PlayerId(1); var second = new PlayerId(2);
        byte[] a = cache.Resolve(first, Resolve, frameImportant);
        byte[] b = cache.Resolve(second, Resolve, frameImportant);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.SameAs(b));
            Assert.That(calls, Is.EqualTo(new[] { first, first, second, second }));
            Assert.That(a, Is.EqualTo(new byte[] { 28, 0, 20, 10, 0, 20, 0, 2, 1, 0,
                29, 191, 9, 7, 8, 21, 0, 18, 0, 36, 0, 5, 0, 64, 2, 0, 0, 0 }));
        });
        cache.Dispose();
        Assert.That(a[10], Is.EqualTo(29), "Packet remains owned and immutable after the scratch rentals return.");
    }

    [Test]
    public void PersonalCompositionCannotBeReusedForDifferentObserverEvenAfterMutation()
    {
        using var cache = new SceneTileSquarePacketCache(0, 0, 1, 1);
        var frameImportant = new bool[64];
        bool personalEnabled = true;
        SceneTile Resolve(PlayerId observer, int x, int y) => new(
            (ushort)(personalEnabled && observer.Slot == 2 ? 42 : 5), Flags: SceneTileFlags.Active);
        byte[] global = cache.Resolve(new PlayerId(1), Resolve, frameImportant);
        byte[] personal = cache.Resolve(new PlayerId(2), Resolve, frameImportant);
        byte[] nextGlobal = cache.Resolve(new PlayerId(3), Resolve, frameImportant);
        personalEnabled = false;
        byte[] changed = cache.Resolve(new PlayerId(2), Resolve, frameImportant);
        Assert.Multiple(() =>
        {
            Assert.That(personal, Is.Not.EqualTo(global));
            Assert.That(personal[14], Is.EqualTo(42));
            Assert.That(nextGlobal, Is.EqualTo(global));
            Assert.That(changed, Is.SameAs(nextGlobal));
            Assert.That(changed[14], Is.EqualTo(5));
        });
    }

    [Test]
    public void FrameImportanceChangeCannotReuseStaleNativeEncoding()
    {
        using var cache = new SceneTileSquarePacketCache(0, 0, 1, 1);
        bool[] frameImportant = new bool[8];
        SceneTile Resolve(PlayerId player, int x, int y) => new(7, FrameX: 18, FrameY: 36, Flags: SceneTileFlags.Active);
        byte[] unframed = cache.Resolve(new PlayerId(1), Resolve, frameImportant);
        frameImportant[7] = true;
        byte[] framed = cache.Resolve(new PlayerId(2), Resolve, frameImportant);
        Assert.Multiple(() =>
        {
            Assert.That(framed.Length, Is.EqualTo(unframed.Length + 4));
            Assert.That(framed[^4..], Is.EqualTo(new byte[] { 18, 0, 36, 0 }));
        });
    }

    [Test]
    public void UnknownSocketReceivesPrivateCopyRatherThanCachedArray()
    {
        using var cache = new SceneTileSquarePacketCache(0, 0, 1, 1);
        byte[] packet = cache.Resolve(new PlayerId(1), (_, _, _) => SceneTile.Air, []);
        var native = new TcpSocket();
        var unknown = new UnknownSocket();
        byte[] nativeBuffer = SceneTileSquarePacketCache.PrepareSendBuffer(native, packet);
        byte[] unknownBuffer = SceneTileSquarePacketCache.PrepareSendBuffer(unknown, packet);
        Assert.That(nativeBuffer, Is.SameAs(packet));
        Assert.That(unknownBuffer, Is.Not.SameAs(packet));
        unknownBuffer[2] = 255;
        Assert.That(packet[2], Is.EqualTo(20));
    }

    private sealed class UnknownSocket : ISocket
    {
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null) => throw new NotSupportedException();
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
    }
}
