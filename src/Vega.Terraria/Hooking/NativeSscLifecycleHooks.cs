using Terraria.Net.Sockets;
using TerrariaRuntime;
using Vega.Contracts.Accounts;
using Vega.Contracts.Operations;
using Vega.Contracts.Threading;
using Vega.Terraria.Players;

namespace Vega.Terraria.Hooking;

/// <summary>Exact connection boundary for pending SSC state and explicit account logout.</summary>
internal sealed class NativeSscLifecycleHooks(TerrariaSandboxSscCharacterAdapter characters, TerrariaPlayerBoundary players,
    IPlayerAccountSessionService accounts, IGameThreadDispatcher dispatcher, IOperationsLogger logger, bool authoritative) : IDisposable
{
    private bool attached;

    public void Attach()
    {
        if (attached) return;
        Hooks.Netplay.ServerPlayerBeforeWorldData += OnBeforeWorldData;
        Hooks.Netplay.ServerConnectionClosing += characters.ForgetCharacterReceived;
        Hooks.Netplay.ServerConnectionClosed += characters.ForgetCharacterReceived;
        if (authoritative)
        {
            Hooks.Netplay.ServerPlayerEnteringWorld += OnEnteringWorld;
            accounts.BindingEnded += OnBindingEnded;
        }
        attached = true;
    }

    public void Dispose()
    {
        if (!attached) return;
        Hooks.Netplay.ServerPlayerBeforeWorldData -= OnBeforeWorldData;
        Hooks.Netplay.ServerConnectionClosing -= characters.ForgetCharacterReceived;
        Hooks.Netplay.ServerConnectionClosed -= characters.ForgetCharacterReceived;
        if (authoritative)
        {
            Hooks.Netplay.ServerPlayerEnteringWorld -= OnEnteringWorld;
            accounts.BindingEnded -= OnBindingEnded;
        }
        attached = false;
    }

    private void OnBeforeWorldData(int slot, ISocket socket)
    {
        characters.ObserveCharacterReceived(slot, socket);
        if (authoritative && !characters.PrepareAuthoritativeJoin(slot, socket)) TerminatePendingCharacter(slot, socket);
    }

    private void OnEnteringWorld(int slot, ISocket socket)
    {
        try
        {
            if (!characters.SynchronizePendingJoin(slot, socket)) TerminatePendingCharacter(slot, socket);
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Error, "ssc", "Could not synchronize pending server character.", ex);
            TerminatePendingCharacter(slot, socket);
        }
    }

    private void TerminatePendingCharacter(int slot, ISocket socket)
    {
        if ((uint)slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[slot] is not { } client || !ReferenceEquals(client.Socket, socket)) return;
        logger.Log(OperationsLogLevel.Warning, "ssc", "Pending server character initialization failed; connection is closing.");
        client.RequestTermination("Server character initialization failed.");
    }

    private void OnBindingEnded(PlayerAccountBinding binding)
    {
        void Reset()
        {
            if (!players.TryGet(binding.PlayerId, out var player) || player?.SessionId != binding.SessionId ||
                accounts.TryGetBinding(player, out _) || !players.TryGetSessionPlayer(player, out _)) return;
            if (!characters.ResetPendingCharacter(player))
                logger.Log(OperationsLogLevel.Warning, "ssc", "Could not clear the pending character after logout; gameplay remains gated.");
        }
        if (dispatcher.IsGameThread) Reset();
        else if (!dispatcher.TryPost("ssc.logout.reset", Reset))
            logger.Log(OperationsLogLevel.Warning, "ssc", "Character reset could not enter the game-thread queue; gameplay remains gated.");
    }
}
