using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Core.Networking;

public sealed class PacketRateLimitPolicy : IPacketProcessor<PacketView>, IDisposable
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyDictionary<PacketTypes, RateLimit> Limits =
        new Dictionary<PacketTypes, RateLimit>
        {
            [PacketTypes.ChestGetContents] = CreateLimit(PacketTypes.ChestGetContents, 2),
            [PacketTypes.PlaceChest] = CreateLimit(PacketTypes.PlaceChest, 3),
            [PacketTypes.SpectatePlayer] = CreateLimit(PacketTypes.SpectatePlayer, 2),
            [PacketTypes.PlayerDeathV2] = CreateLimit(PacketTypes.PlayerDeathV2, 2),
            [PacketTypes.LiquidSet] = CreateLimit(PacketTypes.LiquidSet, 60),
            [PacketTypes.MassWireOperation] = CreateLimit(PacketTypes.MassWireOperation, 60)
        };

    private readonly object sync = new();
    private readonly Dictionary<(ConnectionId Connection, PacketTypes Packet), Window> windows = [];
    private readonly IPlayerSessionLifecycle sessions;
    private bool disposed;

    public PacketRateLimitPolicy(IPlayerSessionLifecycle sessions)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        sessions.Closed += OnSessionClosed;
    }

    public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context)
    {
        if (!Limits.TryGetValue(context.Packet.PacketType, out RateLimit limit))
        {
            return PacketProcessingDecision.Allow;
        }

        lock (sync)
        {
            var key = (context.ConnectionId, context.Packet.PacketType);
            if (!windows.TryGetValue(key, out Window? window))
            {
                windows[key] = new Window(context.Timestamp, 1);
                return PacketProcessingDecision.Allow;
            }

            if (context.Timestamp - window.Start >= WindowLength || context.Timestamp < window.Start)
            {
                window.Start = context.Timestamp;
                window.Count = 1;
                return PacketProcessingDecision.Allow;
            }

            window.Count++;
            return window.Count <= limit.Count
                ? PacketProcessingDecision.Allow
                : limit.Rejection;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        sessions.Closed -= OnSessionClosed;
        lock (sync)
        {
            windows.Clear();
        }

        disposed = true;
    }

    private void OnSessionClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            foreach ((ConnectionId Connection, PacketTypes Packet) key in windows.Keys
                .Where(key => key.Connection.Slot == closed.PlayerId.Slot)
                .ToArray())
            {
                windows.Remove(key);
            }
        }
    }

    private static RateLimit CreateLimit(PacketTypes packetType, int count) =>
        new(count, PacketProcessingDecision.Reject($"packet.rate_limit.{packetType}"));

    private readonly record struct RateLimit(int Count, PacketProcessingDecision Rejection);

    private sealed class Window(DateTimeOffset start, int count)
    {
        public DateTimeOffset Start { get; set; } = start;

        public int Count { get; set; } = count;
    }
}
