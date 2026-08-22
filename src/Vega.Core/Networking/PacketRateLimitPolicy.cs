using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;

namespace Vega.Core.Networking;

public sealed class PacketRateLimitPolicy : IPacketProcessor<PacketView>, IDisposable
{
    private static readonly IReadOnlyDictionary<PacketTypes, int> Limits =
        new Dictionary<PacketTypes, int>
        {
            [PacketTypes.ChestGetContents] = 2,
            [PacketTypes.PlaceChest] = 3,
            [PacketTypes.SpectatePlayer] = 2,
            [PacketTypes.PlayerDeathV2] = 2,
            [PacketTypes.LiquidSet] = 60,
            [PacketTypes.MassWireOperation] = 60
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
        if (!Limits.TryGetValue(context.Packet.PacketType, out int limit))
        {
            return PacketProcessingDecision.Allow;
        }

        lock (sync)
        {
            var key = (context.ConnectionId, context.Packet.PacketType);
            if (!windows.TryGetValue(key, out Window? window) ||
                context.Timestamp - window.Start >= TimeSpan.FromSeconds(1) ||
                context.Timestamp < window.Start)
            {
                windows[key] = new Window(context.Timestamp, 1);
                return PacketProcessingDecision.Allow;
            }

            window.Count++;
            return window.Count <= limit
                ? PacketProcessingDecision.Allow
                : new PacketProcessingDecision(
                    PacketDecisionKind.Reject,
                    $"packet.rate_limit.{context.Packet.PacketType}");
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

    private sealed class Window(DateTimeOffset start, int count)
    {
        public DateTimeOffset Start { get; } = start;

        public int Count { get; set; } = count;
    }
}
