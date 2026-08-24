using TerrariaRuntime;
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
        HookEvents.Terraria.Main.Update += OnGameUpdate;
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnIdleGameUpdate;
        attached = true;
    }

    public ServerRuntimeStateSnapshot Read() => Volatile.Read(ref snapshot);

    internal void ResetTickRateStatistics() => tickRateMonitor.Reset();

    public void Dispose()
    {
        if (attached)
        {
            HookEvents.Terraria.Main.Update -= OnGameUpdate;
            global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnIdleGameUpdate;
            attached = false;
        }
    }

    private void OnGameUpdate(
        global::Terraria.Main instance,
        HookEvents.Terraria.Main.UpdateEventArgs args) => Sample();

    private void OnIdleGameUpdate()
    {
        // Dedicated Terraria skips Main.Update while no client is fully connected.
        // Its third-party tick is the only callback that still runs once per server frame.
        if (!global::Terraria.Netplay.HasFullyConnectedClients)
        {
            Sample();
        }
    }

    private void Sample()
    {
        try
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
        catch (Exception ex)
        {
            // Telemetry is never allowed to tear down Terraria's update loop.
            Console.Error.WriteLine($"Vega operations sampling failed: {ex}");
        }
    }
}
