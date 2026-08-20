using System.Diagnostics;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Operations;

/// <summary>
/// Samples Terraria-owned state on the game thread and publishes one immutable operations snapshot.
/// </summary>
public sealed class TerrariaOperationsState : IServerRuntimeStateReader, IDisposable
{
    private ServerRuntimeStateSnapshot snapshot = new(
        null,
        0,
        0,
        false,
        0,
        DateTimeOffset.UtcNow);
    private long tpsWindowStartedAt;
    private int updatesInWindow;
    private double ticksPerSecond;
    private bool attached;

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        tpsWindowStartedAt = Stopwatch.GetTimestamp();
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnTick;
        attached = true;
    }

    public ServerRuntimeStateSnapshot Read() => Volatile.Read(ref snapshot);

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
        updatesInWindow++;

        long now = Stopwatch.GetTimestamp();
        TimeSpan elapsed = Stopwatch.GetElapsedTime(tpsWindowStartedAt, now);
        if (elapsed >= TimeSpan.FromSeconds(1))
        {
            ticksPerSecond = updatesInWindow / elapsed.TotalSeconds;
            updatesInWindow = 0;
            tpsWindowStartedAt = now;
        }

        Volatile.Write(ref snapshot, new ServerRuntimeStateSnapshot(
            string.IsNullOrWhiteSpace(global::Terraria.Main.worldName)
                ? null
                : global::Terraria.Main.worldName,
            global::Terraria.Main.maxNetPlayers,
            global::Terraria.Netplay.ListenPort,
            !global::Terraria.Main.gameMenu,
            ticksPerSecond,
            DateTimeOffset.UtcNow));
    }
}
