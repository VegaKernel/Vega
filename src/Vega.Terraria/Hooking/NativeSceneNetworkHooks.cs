using Multiplicity.Packets;
using OTAPI;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

internal sealed class NativeSceneNetworkHooks(
    ISceneRuntime scenes,
    ISceneRenderSink renderer,
    IPlayerDirectory players) : IDisposable
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
            foreach (SceneOverlayDefinition overlay in scenes.SnapshotScenes()
                         .Where(scene => scene.WorldId == WorldId.Primary)
                         .SelectMany(static scene => scene.Overlays)
                         .Where(overlay => overlay.Enabled && overlay.Bounds.Intersects(section)))
            {
                renderer.Invalidate(Request(overlay.Bounds, args.RemoteClient, args.IgnoreClient));
            }
        }
    }

    private SceneRenderRequest Request(SceneBounds bounds, int remoteClient, int ignoreClient)
    {
        if (remoteClient >= 0)
        {
            return new SceneRenderRequest(
                default,
                WorldId.Primary,
                bounds,
                false,
                new HashSet<PlayerId> { new(remoteClient) });
        }

        return new SceneRenderRequest(
            default,
            WorldId.Primary,
            bounds,
            false,
            new HashSet<PlayerId>(players.SnapshotOnlinePlayers()
                .Select(static player => player.Id)
                .Where(playerId => playerId.Slot != ignoreClient)));
    }
}
