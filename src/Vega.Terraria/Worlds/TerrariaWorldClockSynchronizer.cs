using Terraria.ID;
using TerrariaRuntime;
using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaWorldClockSynchronizer(
    IWorldClockController clock,
    IOperationsLogger logger,
    WorldId? worldId = null) : IDisposable
{
    private readonly WorldId currentWorld = worldId ?? WorldId.Primary;
    private bool attached;
    private bool observedWorld;

    public void Attach()
    {
        if (attached || !clock.Enabled)
        {
            return;
        }

        HookEvents.Terraria.Main.Update += OnGameUpdate;
        global::Terraria.Main.OnTickForThirdPartySoftwareOnly += OnIdleTick;
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
            HookEvents.Terraria.Main.Update -= OnGameUpdate;
            global::Terraria.Main.OnTickForThirdPartySoftwareOnly -= OnIdleTick;
            attached = false;
        }

        clock.Reset(currentWorld);
    }

    private void OnGameUpdate(
        global::Terraria.Main instance,
        HookEvents.Terraria.Main.UpdateEventArgs args) => OnTick();

    private void OnIdleTick()
    {
        if (!global::Terraria.Netplay.HasFullyConnectedClients)
        {
            OnTick();
        }
    }

    private void OnTick()
    {
        if (global::Terraria.Main.gameMenu)
        {
            if (observedWorld)
            {
                clock.Reset(currentWorld);
                observedWorld = false;
            }
            return;
        }

        observedWorld = true;
        double phaseLimit = global::Terraria.Main.dayTime ? 54000d : 32400d;
        var observed = new WorldClockValue(
            global::Terraria.Main.dayTime,
            Math.Clamp(global::Terraria.Main.time, 0, phaseLimit));
        WorldClockDecision decision = clock.Observe(currentWorld, observed);
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
