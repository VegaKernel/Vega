using Vega.Contracts.Operations;

namespace Vega.Terraria.Operations;

/// <summary>
/// Samples Terraria-owned state on the game thread and publishes one immutable operations snapshot.
/// </summary>
public sealed class TerrariaOperationsState : IServerRuntimeStateReader, IDisposable
{
    private readonly TickRateMonitor tickRateMonitor;
    private ServerRuntimeStateSnapshot snapshot = new(
        null,
        0,
        0,
        false,
        0,
        DateTimeOffset.UtcNow);
    private bool attached;

    public TerrariaOperationsState()
        : this(new TickRateMonitor())
    {
    }

    internal TerrariaOperationsState(TickRateMonitor tickRateMonitor)
    {
        this.tickRateMonitor = tickRateMonitor ?? throw new ArgumentNullException(nameof(tickRateMonitor));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        tickRateMonitor.Reset();
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnTick;
        attached = true;
    }

    public ServerRuntimeStateSnapshot Read() => Volatile.Read(ref snapshot);

    internal void ResetTickRateStatistics() => tickRateMonitor.Reset();

    public void Dispose()
    {
        if (attached)
        {
            global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnTick;
            attached = false;
        }
    }

    private void OnTick()
    {
        TickRateStatisticsSnapshot tickRate = tickRateMonitor.RecordTick();

        Volatile.Write(ref snapshot, new ServerRuntimeStateSnapshot(
            string.IsNullOrWhiteSpace(global::Terraria.Main.worldName)
                ? null
                : global::Terraria.Main.worldName,
            global::Terraria.Main.maxNetPlayers,
            global::Terraria.Netplay.ListenPort,
            !global::Terraria.Main.gameMenu,
            tickRate.Current,
            DateTimeOffset.UtcNow)
        {
            TickRate = tickRate
        });
    }
}
