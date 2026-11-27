using Multiplicity.Packets;
using TerrariaRuntime;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

internal sealed class NativeSceneNetworkHooks(
    ISceneRuntime scenes,
    ISceneRenderSink renderer,
    IPlayerDirectory players,
    WorldId worldId,
    IGameThreadDispatcher dispatcher) : IDisposable
{
    private bool attached;

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.NetMessage.SendData += OnSendData;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.NetMessage.SendData -= OnSendData;
        attached = false;
    }

    private void OnSendData(object? sender, Hooks.NetMessage.SendDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel || !scenes.HasActiveOverlays)
        {
            return;
        }

        if (args.MsgType == (int)PacketTypes.UpdateTileEntity && args.Event == HookEvent.After && dispatcher.IsGameThread &&
            scenes.HasVirtualItemFrames(worldId) && global::Terraria.DataStructures.TileEntity.ByID.TryGetValue(args.Number, out var entity))
        {
            // A canonical entity can appear at a previously projected position. Reconcile
            // after its native update without altering canonical registration or delivery.
            var bounds = new SceneBounds(entity.Position.X, entity.Position.Y, 2, 2);
            if (scenes.HasOverlayInRegion(worldId, bounds, args.RemoteClient >= 0 ? new PlayerId(args.RemoteClient) : null))
                renderer.Invalidate(Request(bounds, args.RemoteClient, args.IgnoreClient));
            return;
        }

        if (args.MsgType == (int)PacketTypes.TileSendSquare && args.Event == HookEvent.Before)
        {
            var bounds = new SceneBounds(
                (int)args.Number,
                (int)args.Number2,
                Math.Max(1, (int)args.Number3),
                Math.Max(1, (int)args.Number4));
            if (!scenes.HasOverlayInRegion(worldId, bounds,
                    args.RemoteClient >= 0 ? new PlayerId(args.RemoteClient) : null)) return;
            // Keep the canonical native update when bounded redraw admission fails.
            // Scenes are visual; losing the original update harms ordinary players too.
            if (renderer.TryInvalidate(Request(bounds, args.RemoteClient, args.IgnoreClient)))
                args.Result = HookResult.Cancel;
            return;
        }

        if (args.MsgType == (int)PacketTypes.TileSendSection && args.Event == HookEvent.After)
        {
            var section = new SceneBounds(
                (int)args.Number,
                (int)args.Number2,
                Math.Max(1, (int)args.Number3),
                Math.Max(1, (int)args.Number4));
            PlayerId? recipient = args.RemoteClient >= 0 ? new PlayerId(args.RemoteClient) : null;
            if (!scenes.HasOverlayInRegion(worldId, section, recipient)) return;
            var sectionRequest = Request(section, args.RemoteClient, args.IgnoreClient);
            IReadOnlySet<PlayerId> ordinary;
            if (recipient is PlayerId player)
            {
                if (scenes.HasPersonalAirMask(worldId, player))
                {
                    renderer.Invalidate(sectionRequest);
                    return;
                }
                ordinary = sectionRequest.Observers;
            }
            else
            {
                var masked = sectionRequest.Observers.Where(id => scenes.HasPersonalAirMask(worldId, id)).ToHashSet();
                if (masked.Count != 0) renderer.Invalidate(sectionRequest with { Observers = masked });
                ordinary = masked.Count == 0 ? sectionRequest.Observers :
                    sectionRequest.Observers.Where(id => !masked.Contains(id)).ToHashSet();
            }
            if (ordinary.Count == 0) return;
            foreach (SceneOverlayDefinition overlay in scenes.EnumerateOverlaysInRegion(worldId, section))
            {
                if (!overlay.Bounds.TryIntersect(section, out var intersection)) continue;
                var request = sectionRequest with { DirtyRegion = intersection, Observers = ordinary };
                if (!overlay.Observers.IsGlobal)
                {
                    if (recipient is PlayerId direct)
                    {
                        if (!overlay.Observers.Contains(direct)) continue;
                    }
                    else request = request with { Observers = request.Observers.Where(overlay.Observers.Contains).ToHashSet() };
                }
                if (request.Observers.Count != 0) renderer.Invalidate(request);
            }
        }
    }

    private SceneRenderRequest Request(SceneBounds bounds, int remoteClient, int ignoreClient)
    {
        if (remoteClient >= 0)
        {
            return new SceneRenderRequest(
                default,
                worldId,
                bounds,
                false,
                SceneObserverScope.Personal(new PlayerId(remoteClient)).Players);
        }

        return new SceneRenderRequest(
            default,
            worldId,
            bounds,
            false,
            new HashSet<PlayerId>(players.SnapshotOnlinePlayers()
                .Where(player => player.WorldId == worldId)
                .Select(static player => player.Id)
                .Where(playerId => playerId.Slot != ignoreClient)));
    }
}
