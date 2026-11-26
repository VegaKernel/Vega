using Multiplicity.Packets;
using TerrariaRuntime;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

internal sealed class NativeSceneNetworkHooks(
    ISceneRuntime scenes,
    ISceneRenderSink renderer,
    IPlayerDirectory players,
    WorldId worldId) : IDisposable
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

        if (args.MsgType == (int)PacketTypes.TileSendSquare && args.Event == HookEvent.Before)
        {
            var bounds = new SceneBounds(
                (int)args.Number,
                (int)args.Number2,
                Math.Max(1, (int)args.Number3),
                Math.Max(1, (int)args.Number4));
            if (!scenes.HasOverlayInRegion(worldId, bounds,
                    args.RemoteClient >= 0 ? new PlayerId(args.RemoteClient) : null)) return;
            renderer.Invalidate(Request(bounds, args.RemoteClient, args.IgnoreClient));
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
            foreach (SceneOverlayDefinition overlay in scenes.SnapshotScenes()
                         .Where(scene => scene.WorldId == worldId)
                         .SelectMany(static scene => scene.Overlays)
                         .Where(overlay => overlay.Enabled))
            {
                if (!overlay.Bounds.TryIntersect(section, out var intersection)) continue;
                var request = Request(intersection, args.RemoteClient, args.IgnoreClient);
                if (!overlay.Observers.IsGlobal)
                    request = request with { Observers = request.Observers.Where(overlay.Observers.Contains).ToHashSet() };
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
                new HashSet<PlayerId> { new(remoteClient) });
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
