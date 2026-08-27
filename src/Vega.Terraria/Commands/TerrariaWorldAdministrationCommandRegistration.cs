using Vega.Contracts.Commands;
using Vega.Contracts.Worlds;
using Vega.Terraria.Players;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Terraria.Commands;

internal static class TerrariaWorldAdministrationCommandRegistration
{
    public static IDisposable Register(
        ICommandDispatcher dispatcher,
        WorldId runtimeWorldId,
        TerrariaRuntimeGameThreadDispatcher gameThread,
        TerrariaPlayerBoundary players,
        IWorldBuildProtectionService buildProtection,
        TerrariaWorldMaintenanceService worldMaintenance)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(gameThread);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(buildProtection);
        ArgumentNullException.ThrowIfNull(worldMaintenance);

        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must expose ICommandRegistry for built-in world administration commands.");
        }

        IWorldEnvironmentService environment = new TerrariaWorldEnvironmentService(gameThread, runtimeWorldId);
        IWorldAdministrationService worldAdministration = new TerrariaWorldAdministrationService(
            gameThread,
            runtimeWorldId,
            worldMaintenance);

        ICommandHandler[] handlers =
        [
            new GrowCommand(worldAdministration),
            new ForceHalloweenCommand(worldAdministration, players, players),
            new ForceXmasCommand(worldAdministration, players, players),
            new WorldEventCommand(worldAdministration, players, players),
            new HardmodeCommand(worldAdministration),
            new EvilCommand(worldAdministration),
            new ProtectSpawnCommand(environment, buildProtection, players, players),
            new SetSpawnCommand(worldAdministration, buildProtection),
            new SetDungeonCommand(worldAdministration),
            new SettleCommand(worldAdministration)
        ];

        return new RegistrationSet(handlers.Select(registry.Register).ToArray());
    }

    private sealed class RegistrationSet(IDisposable[] registrations) : IDisposable
    {
        private IDisposable[]? owned = registrations;

        public void Dispose()
        {
            IDisposable[]? snapshot = Interlocked.Exchange(ref owned, null);
            if (snapshot is null)
            {
                return;
            }

            for (int index = snapshot.Length - 1; index >= 0; index--)
            {
                snapshot[index].Dispose();
            }
        }
    }
}