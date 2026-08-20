using Terraria.ID;
using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldClockSynchronizer(
    IWorldClockController clock,
    IOperationsLogger logger) : IDisposable
{
    private bool attached;
    private bool observedWorld;

    public void Attach()
    {
        if (attached || !clock.Enabled)
        {
            return;
        }

        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnTick;
        attached = true;
        logger.Log(
            OperationsLogLevel.Information,
            "world-clock",
            "Monotonic primary-world clock synchronization is enabled.");
    }

    public void Dispose()
    {
        if (attached)
        {
            global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnTick;
            attached = false;
        }

        clock.Reset(WorldId.Primary);
    }

    private void OnTick()
    {
        if (global::Terraria.Main.gameMenu)
        {
            if (observedWorld)
            {
                clock.Reset(WorldId.Primary);
                observedWorld = false;
            }
            return;
        }

        observedWorld = true;
        double phaseLimit = global::Terraria.Main.dayTime ? 54000d : 32400d;
        var observed = new WorldClockValue(
            global::Terraria.Main.dayTime,
            Math.Clamp(global::Terraria.Main.time, 0, phaseLimit));
        WorldClockDecision decision = clock.Observe(WorldId.Primary, observed);
        if (decision.Action != WorldClockAction.Correct)
        {
            return;
        }

        global::Terraria.Main.dayTime = decision.Target.IsDaytime;
        global::Terraria.Main.time = decision.Target.TimeTicks;
        if (global::Terraria.Main.dedServ)
        {
            global::Terraria.NetMessage.SendData(MessageID.WorldData);
        }
    }
}
