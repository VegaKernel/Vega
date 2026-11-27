using Multiplicity.Packets;
using TerrariaRuntime;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Operations;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Hooking;

internal sealed class NativeSceneNetworkHooks(
    ISceneRuntime scenes,
    ISceneRenderSink renderer,
    IPlayerDirectory players,
    WorldId worldId,
    IGameThreadDispatcher dispatcher,
    IOperationsLogger logger) : IDisposable
{
    private bool attached;

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.NetMessage.SendData += OnSendData;
        Hooks.Main.DedicatedTickCompleted += OnTickCompleted;
        Hooks.MessageBuffer.ServerProjectileSynced += OnProjectileSynced;
        Hooks.MessageBuffer.ServerProjectileRemoved += OnProjectileRemoved;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.NetMessage.SendData -= OnSendData;
        Hooks.Main.DedicatedTickCompleted -= OnTickCompleted;
        Hooks.MessageBuffer.ServerProjectileSynced -= OnProjectileSynced;
        Hooks.MessageBuffer.ServerProjectileRemoved -= OnProjectileRemoved;
        attached = false;
    }

    private void OnTickCompleted(TimeSpan duration)
    {
        if (!dispatcher.IsGameThread) return;
        if (scenes is IVirtualChestGuiAuthority { HasManagedChestPlayers: true } authority)
            try { authority.TickManagedChestGuis(players, DateTimeOffset.UtcNow); }
            catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "scenes", "Managed virtual chest reconciliation failed.", ex); }
        if (scenes is ISceneGestureRuntime { HasPendingGestures: true } gestures && players is ISceneGestureToolState tool)
            try { gestures.TickGestures(players, tool, DateTimeOffset.UtcNow); }
            catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "scenes", "Virtual gesture reconciliation failed.", ex); }
    }

    private void OnProjectileSynced(int sender, global::Terraria.Projectile projectile)
    {
        if (!dispatcher.IsGameThread || scenes is not ISceneGestureRuntime { HasGestureTargets: true } gestures ||
            projectile.owner != sender || projectile.type != global::Terraria.ID.ProjectileID.WireKite ||
            !projectile.active || players is not ISceneGestureToolState tool || !players.TryGet(new(sender), out var player) ||
            player is null || player.WorldId != worldId || !tool.IsHoldingGestureTool(player)) return;
        var position = projectile.Center;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || position.X < 0 || position.Y < 0 ||
            position.X >= global::Terraria.Main.maxTilesX * 16f || position.Y >= global::Terraria.Main.maxTilesY * 16f)
        { gestures.CancelGesture(player, projectile.whoAmI, projectile.key, DateTimeOffset.UtcNow); return; }
        try { gestures.ObserveGesturePosition(player, projectile.whoAmI, projectile.key,
            new((int)(position.X / 16f), (int)(position.Y / 16f)), DateTimeOffset.UtcNow); }
        catch (Exception ex) { logger.Log(OperationsLogLevel.Warning, "scenes", "Virtual gesture input failed.", ex); }
    }

    private void OnProjectileRemoved(int sender, global::Terraria.Projectile projectile)
    {
        if (!dispatcher.IsGameThread || scenes is not ISceneGestureRuntime { HasPendingGestures: true } gestures ||
            projectile.owner != sender || !players.TryGet(new(sender), out var player) || player is null || player.WorldId != worldId) return;
        gestures.CancelGesture(player, projectile.whoAmI, projectile.key, DateTimeOffset.UtcNow);
    }

    private void OnSendData(object? sender, Hooks.NetMessage.SendDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel) return;
        if (args.MsgType == (int)PacketTypes.SignNew && args.Event == HookEvent.After && dispatcher.IsGameThread && args.RemoteClient >= 0 &&
            (uint)args.Number < (uint)global::Terraria.Main.sign.Length && global::Terraria.Main.sign[args.Number] is { } canonical &&
            players.TryGet(new PlayerId(args.RemoteClient), out var reader) && reader is not null && reader.WorldId == worldId)
            scenes.ObserveCanonicalSignPresented(reader, checked((short)args.Number), canonical.x, canonical.y);
        if (!scenes.HasActiveOverlays)
        {
            return;
        }

        if (args.MsgType == (int)PacketTypes.UpdateTileEntity && args.Event == HookEvent.After && dispatcher.IsGameThread &&
            (scenes.HasVirtualItemFrames(worldId) || scenes.HasVirtualDisplays(worldId)) && global::Terraria.DataStructures.TileEntity.ByID.TryGetValue(args.Number, out var entity))
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
