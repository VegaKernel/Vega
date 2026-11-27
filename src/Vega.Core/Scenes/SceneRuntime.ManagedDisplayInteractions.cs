using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;

namespace Vega.Core.Scenes;

public sealed partial class SceneRuntime : IManagedDisplayAuthority
{
    private sealed class ManagedDisplaySession(SceneManagedDisplayTarget target, object definition, SceneSessionObserver observer, DateTimeOffset now)
    {
        public SceneManagedDisplayTarget Target { get; } = target;
        public object Definition { get; } = definition;
        public SceneSessionObserver Observer { get; } = observer;
        public DateTimeOffset ArmedAt { get; } = now;
        public DateTimeOffset? PendingAt;
        public SceneDisplayItem? Placement;
        public InventorySlotReplacement? InventoryProposal;
        public SceneManagedDisplayTransferIntent? Ready;
        public bool Delivered, Claimed, Cancelled;
    }

    public bool TryResolveManagedDisplayTarget(PlayerSnapshot player, SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind,
        out SceneManagedDisplayTarget target)
    {
        lock (sync) return TryResolveManagedDisplayTargetLocked(player, scene, overlay, entity, kind, out target, out _);
    }

    private bool TryResolveManagedDisplayTargetLocked(PlayerSnapshot player, SceneId sceneId, OverlayId overlayId, Guid entity,
        ManagedDisplayKind kind, out SceneManagedDisplayTarget target, out object definition)
    {
        target = default; definition = null!;
        if (!IsCurrentObserver(player) || player.IsDead || !scenes.TryGetValue(sceneId, out var scene) || scene.WorldId != player.WorldId ||
            !scene.Overlays.TryGetValue(overlayId, out var provider) || !provider.Definition.Enabled ||
            !ReadLookup().TryGetValue(player.WorldId, out var world)) return false;
        var overlay = provider.Definition;
        int localX, localY, width, height;
        long revision;
        if (kind == ManagedDisplayKind.ItemFrame && overlay.VirtualItemFrames.FirstOrDefault(item => item.Id.Value == entity) is { ItemType: 0 } frame)
        { definition = frame; localX = frame.LocalX; localY = frame.LocalY; width = height = 2; revision = frame.Revision; }
        else if (kind == ManagedDisplayKind.WeaponsRack && overlay.VirtualDisplays.FirstOrDefault(item => item.Id.Value == entity && item.Kind == SceneVirtualDisplayKind.WeaponsRack) is { } rack &&
            rack.Items.All(item => item.ItemType == 0))
        { definition = rack; localX = rack.LocalX; localY = rack.LocalY; width = rack.Width; height = rack.Height; revision = rack.Revision; }
        else return false;
        int x = overlay.Bounds.X + localX, y = overlay.Bounds.Y + localY;
        if (!WithinObjectReach(player,x,y) || !FootprintVisible(world,player.Id,overlay,localX,localY,width,height)) return false;
        target = new(sceneId,overlayId,player.WorldId,new(x,y),entity,kind,revision);
        return true;
    }

    public bool TryArmManagedDisplay(PlayerSnapshot player, SceneManagedDisplayTarget target, PlayerInventorySnapshot inventory, IVirtualChestGuiHost host)
    {
        if (!InventoryMatchesSession(player, inventory)) return false;
        lock (sync)
        {
            if (!TryResolveManagedDisplayTargetLocked(player,target.SceneId,target.OverlayId,target.EntityId,target.Kind,out var current,out var definition) || current != target)
                return false;
            var observer = SceneSessionObserver.From(player);
            var key = (player.WorldId,player.Id);
            if (!managedChestPlayers.TryGetValue(key,out var managed))
            {
                managedChestPlayers[key] = managed = new ManagedChestPlayer(observer.Session,host);
                Interlocked.Increment(ref managedChestCount);
            }
            if (managed.Session != observer.Session || !ReferenceEquals(managed.Host,host) || managed.Gui is not null || managed.Display is not null) return false;
            managed.Inventory = inventory;
            managed.Display = new(target,definition,observer,DateTimeOffset.UtcNow);
            return true;
        }
    }

    public bool ObserveManagedDisplayPlacement(PlayerSnapshot player, ManagedDisplayKind kind, int x, int y, SceneDisplayItem proposed, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player,out var state)) return false;
            if (state.Display is not { Cancelled: false } display)
            {
                if (state.Gui is { } gui) gui.Closing = true;
                return true;
            }
            if (display.Target.Kind != kind || !DisplayBounds(display.Target).Contains(x,y) || proposed.ItemType <= 0 ||
                !ValidStack(proposed.ItemType,proposed.Stack,proposed.Prefix)) { display.Cancelled = true; return true; }
            if (display.Placement is { } repeated && repeated == proposed) return true;
            if (display.Ready is not null || display.Placement is not null) { display.Cancelled = true; return true; }
            display.Placement = proposed;
            display.PendingAt ??= now;
            TryPairManagedDisplay(state,display,now);
            return true;
        }
    }

    private static void ObserveManagedDisplayInventory(ManagedChestPlayer state, ManagedDisplaySession display, InventorySlotReplacement proposed, DateTimeOffset now)
    {
        if (display.Cancelled || state.Inventory is not { } inventory || !TryInventorySlot(inventory,proposed.Slot,out var before))
        { AddCorrection(state,proposed.Slot); display.Cancelled = true; return; }
        if (before.ItemType == proposed.ItemType && before.Stack == proposed.Stack && before.Prefix == proposed.Prefix && before.Favorited == proposed.Favorited) return;
        if (display.InventoryProposal is { } repeated && repeated == proposed) return;
        if (!ValidStack(proposed.ItemType,proposed.Stack,proposed.Prefix) || display.Ready is not null || display.InventoryProposal is not null)
        { AddCorrection(state,proposed.Slot); display.Cancelled = true; return; }
        display.InventoryProposal = proposed;
        display.PendingAt ??= now;
        TryPairManagedDisplay(state,display,now);
    }

    private static void TryPairManagedDisplay(ManagedChestPlayer state, ManagedDisplaySession display, DateTimeOffset now)
    {
        if (display.Placement is not { } item || display.InventoryProposal is not { } inventory || state.Inventory is not { } snapshot) return;
        if (now - display.PendingAt!.Value > GuiPairLifetime || !TryInventorySlot(snapshot,inventory.Slot,out var before) ||
            !TryPlanBalancedChestMovement(new(0,0,0,0),new(0,item.ItemType,item.Stack,item.Prefix),before,inventory,out var direction,out int amount) ||
            direction != VirtualChestTransferDirection.Deposit || amount != item.Stack)
        { display.Cancelled = true; return; }
        display.Ready = new(Guid.NewGuid(),display.Target,display.Observer,snapshot.Revision,inventory.Slot,item,now);
        display.PendingAt = now;
    }

    private static SceneBounds DisplayBounds(SceneManagedDisplayTarget target) => new(target.Position.X,target.Position.Y,
        target.Kind == ManagedDisplayKind.ItemFrame ? 2 : 3,target.Kind == ManagedDisplayKind.ItemFrame ? 2 : 3);

    public bool TryTakeManagedDisplayTransfer(SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind, out SceneManagedDisplayTransferIntent? intent)
    {
        lock (sync)
        {
            foreach (var state in managedChestPlayers.Values)
                if (state.Display is { Cancelled: false, Delivered: false, Ready: { } ready } display && ready.Target.SceneId == scene &&
                    ready.Target.OverlayId == overlay && ready.Target.EntityId == entity && ready.Target.Kind == kind &&
                    DateTimeOffset.UtcNow - ready.RequestedAt <= VirtualInteractionLifetime)
                { display.Delivered = true; intent = ready; return true; }
            intent = null; return false;
        }
    }

    public bool TryClaimManagedDisplayTransfer(SceneManagedDisplayTransferIntent intent)
    {
        lock (sync)
        {
            if (!TryGetDisplayIntent(intent,out var display) || !display.Delivered || display.Claimed || DateTimeOffset.UtcNow - intent.RequestedAt > VirtualInteractionLifetime) return false;
            display.Claimed = true; return true;
        }
    }

    private bool TryGetDisplayIntent(SceneManagedDisplayTransferIntent intent, out ManagedDisplaySession display)
    {
        if (managedChestPlayers.TryGetValue((intent.Target.WorldId,intent.Observer.Player),out var state) && state.Session == intent.Observer.Session &&
            state.Display is { Cancelled: false, Ready: { } ready } found && ready == intent)
        { display = found; return true; }
        display = null!; return false;
    }

    public bool RejectManagedDisplayTransfer(SceneManagedDisplayTransferIntent intent)
    {
        lock (sync)
        {
            if (!TryGetDisplayIntent(intent,out var display) || !display.Delivered) return false;
            display.Cancelled = true; return true;
        }
    }

    public bool CancelManagedDisplay(PlayerSnapshot player, SceneId scene, OverlayId overlay, Guid entity, ManagedDisplayKind kind)
    {
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player,out var state) || state.Display is not { } display || display.Target.SceneId != scene ||
                display.Target.OverlayId != overlay || display.Target.EntityId != entity || display.Target.Kind != kind) return false;
            display.Cancelled = true; return true;
        }
    }

    public bool TryCommitManagedDisplayTransfer(PlayerSnapshot player, SceneManagedDisplayTransferIntent intent, Action commitInventory,
        out SceneManagedDisplayTransferResult result)
    {
        result = new(ManagedDisplayTransferStatus.StaleInteraction);
        SceneRenderRequest redraw;
        var target = intent.Target;
        lock (sync)
        {
            if (!TryGetManagedChestPlayer(player,out var state) || !TryGetDisplayIntent(intent,out var display) || !display.Claimed ||
                player.SessionId != intent.Observer.Session || player.Id != intent.Observer.Player || DateTimeOffset.UtcNow - intent.RequestedAt > VirtualInteractionLifetime ||
                !TryResolveManagedDisplayTargetLocked(player,target.SceneId,target.OverlayId,target.EntityId,target.Kind,out var current,out var definition) ||
                current != target || !ReferenceEquals(definition,display.Definition)) return false;
            var scene = scenes[target.SceneId];
            var previous = scene.Overlays[target.OverlayId];
            SceneOverlayDefinition next;
            if (definition is SceneVirtualItemFrameDefinition frame)
            {
                var updated = new SceneVirtualItemFrameDefinition(frame.Id,frame.LocalX,frame.LocalY,intent.Item.ItemType,intent.Item.Stack,intent.Item.Prefix,checked(frame.Revision+1));
                next = previous.Definition with { VirtualItemFrames = Array.AsReadOnly(previous.Definition.VirtualItemFrames.Select(item => item.Id == frame.Id ? updated : item).ToArray()) };
                result = new(ManagedDisplayTransferStatus.Applied,ItemFrame: updated);
            }
            else
            {
                var rack = (SceneVirtualDisplayDefinition)definition;
                var updated = new SceneVirtualDisplayDefinition(rack.Id,rack.Kind,rack.LocalX,rack.LocalY,[intent.Item],rack.Dyes,checked(rack.Revision+1));
                next = previous.Definition with { VirtualDisplays = Array.AsReadOnly(previous.Definition.VirtualDisplays.Select(item => item.Id == rack.Id ? updated : item).ToArray()) };
                result = new(ManagedDisplayTransferStatus.Applied,Display: updated);
            }
            var replacement = previous with { Definition = next };
            redraw = RenderRequest(scene,DisplayBounds(target),next.Observers);
            commitInventory();
            scene.Overlays[target.OverlayId] = replacement;
            Volatile.Write(ref lookup,null);
            RecordDirty(scene,DisplayBounds(target));
            state.Display = null;
            state.Inventory = null;
        }
        // State is committed even if a congested renderer cannot admit this visual refresh.
        try { if (sink is not null && !sink.TryInvalidate(redraw)) result = result with { Error = "display.refresh_unavailable" }; }
        catch (Exception) { ReportFailure(target.SceneId,"Managed display refresh failed."); result = result with { Error = "display.refresh_failed" }; }
        return true;
    }

    private bool TryExpireManagedDisplay(ManagedChestPlayer state, PlayerSnapshot player, DateTimeOffset now, out SceneRenderRequest? redraw)
    {
        redraw = null;
        if (state.Display is not { } display) return false;
        if (!display.Cancelled && now - display.ArmedAt <= GuiIdleLifetime &&
            (display.PendingAt is null || now - display.PendingAt.Value <= (display.Ready is null ? GuiPairLifetime : VirtualInteractionLifetime)) &&
            TryResolveManagedDisplayTargetLocked(player,display.Target.SceneId,display.Target.OverlayId,display.Target.EntityId,display.Target.Kind,out var current,out var definition) &&
            current == display.Target && ReferenceEquals(display.Definition,definition)) return false;
        state.Display = null;
        state.Inventory = null;
        for (int slot=0;slot<=ManagedMouseSlot;slot++) AddCorrection(state,slot);
        redraw = new SceneRenderRequest(display.Target.SceneId,display.Target.WorldId,DisplayBounds(display.Target),false,SceneObserverScope.Personal(player.Id).Players);
        return true;
    }
}
