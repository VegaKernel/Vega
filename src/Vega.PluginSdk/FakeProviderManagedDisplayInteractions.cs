using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

/// <summary>Opt-in one-shot placement into an empty display, with managed SSC inventory until session disconnect.</summary>
public interface IPluginManagedDisplayInteraction
{
    bool TryArmManagedInteraction(PlayerSnapshot player) => false;
    bool TryTakeManagedTransfer(out SceneManagedDisplayTransferIntent? intent) { intent = null; return false; }
    SceneManagedDisplayTransferResult AcceptManagedTransfer(SceneManagedDisplayTransferIntent intent) => new(ManagedDisplayTransferStatus.Unavailable);
    bool RejectManagedTransfer(SceneManagedDisplayTransferIntent intent) => false;
    bool CancelManagedInteraction(PlayerSnapshot player) => false;
}

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        private sealed partial class TileProvider
        {
            private bool OwnsManagedDisplay(Guid entity, ManagedDisplayKind kind)
            {
                if (kind == ManagedDisplayKind.ItemFrame)
                {
                    var frames = definition.VirtualItemFrames;
                    for(int i=0;i<frames.Count;i++) if (frames[i].Id.Value == entity) return true;
                }
                else if (kind == ManagedDisplayKind.WeaponsRack)
                {
                    var displays = definition.VirtualDisplays;
                    for(int i=0;i<displays.Count;i++) if (displays[i].Id.Value == entity && displays[i].Kind == SceneVirtualDisplayKind.WeaponsRack) return true;
                }
                return false;
            }

            private bool OwnsManagedDisplay(Guid entity, ManagedDisplayKind kind, SceneManagedDisplayTransferIntent intent) =>
                intent.Target.SceneId == Scene.Id && intent.Target.OverlayId == Id && intent.Target.EntityId == entity && intent.Target.Kind == kind && OwnsManagedDisplay(entity,kind);

            private bool ArmDisplay(Guid entity, ManagedDisplayKind kind, PlayerSnapshot player) => Read(() =>
                OwnsManagedDisplay(entity,kind) && Services.Inventory is IManagedDisplayInventoryHost host &&
                host.TryArmManagedDisplay(sceneRuntime,player,Scene.Id,Id,entity,kind));

            private (bool Found, SceneManagedDisplayTransferIntent? Intent) TakeDisplayTransfer(Guid entity, ManagedDisplayKind kind)
            {
                lock (Owner.sync)
                {
                    AssertAlive();
                    if (!OwnsManagedDisplay(entity,kind) || sceneRuntime is not IManagedDisplayAuthority authority) return (false,null);
                    bool found = authority.TryTakeManagedDisplayTransfer(Scene.Id,Id,entity,kind,out var intent);
                    return (found,intent);
                }
            }

            private SceneManagedDisplayTransferResult AcceptDisplayTransfer(Guid entity, ManagedDisplayKind kind, SceneManagedDisplayTransferIntent intent) => Read(() =>
            {
                if (!OwnsManagedDisplay(entity,kind,intent)) return new SceneManagedDisplayTransferResult(ManagedDisplayTransferStatus.StaleInteraction);
                if (Services.Inventory is not IManagedDisplayInventoryHost host) return new SceneManagedDisplayTransferResult(ManagedDisplayTransferStatus.Unavailable);
                var result = host.AcceptManagedDisplayTransfer(sceneRuntime,intent);
                if (result.Status == ManagedDisplayTransferStatus.Applied)
                {
                    if (result.ItemFrame is { } frame) definition = definition with
                    { VirtualItemFrames = Array.AsReadOnly(definition.VirtualItemFrames.Select(old => old.Id == frame.Id ? frame : old).ToArray()) };
                    if (result.Display is { } display) definition = definition with
                    { VirtualDisplays = Array.AsReadOnly(definition.VirtualDisplays.Select(old => old.Id == display.Id ? display : old).ToArray()) };
                }
                return result;
            });

            private bool RejectDisplayTransfer(Guid entity,ManagedDisplayKind kind,SceneManagedDisplayTransferIntent intent) => Read(() =>
                OwnsManagedDisplay(entity,kind,intent) && sceneRuntime is IManagedDisplayAuthority authority && authority.RejectManagedDisplayTransfer(intent));

            private bool CancelDisplay(Guid entity,ManagedDisplayKind kind,PlayerSnapshot player) => Read(() =>
                OwnsManagedDisplay(entity,kind) && sceneRuntime is IManagedDisplayAuthority authority && authority.CancelManagedDisplay(player,Scene.Id,Id,entity,kind));

            private sealed partial class VirtualItemFrame
            {
                public bool TryArmManagedInteraction(PlayerSnapshot player) { AssertOpen(); return provider.ArmDisplay(id.Value,ManagedDisplayKind.ItemFrame,player); }
                public bool TryTakeManagedTransfer(out SceneManagedDisplayTransferIntent? intent)
                { AssertOpen(); var result = provider.TakeDisplayTransfer(id.Value,ManagedDisplayKind.ItemFrame); intent = result.Intent; return result.Found; }
                public SceneManagedDisplayTransferResult AcceptManagedTransfer(SceneManagedDisplayTransferIntent intent)
                { AssertOpen(); return provider.AcceptDisplayTransfer(id.Value,ManagedDisplayKind.ItemFrame,intent); }
                public bool RejectManagedTransfer(SceneManagedDisplayTransferIntent intent) { AssertOpen(); return provider.RejectDisplayTransfer(id.Value,ManagedDisplayKind.ItemFrame,intent); }
                public bool CancelManagedInteraction(PlayerSnapshot player) { AssertOpen(); return provider.CancelDisplay(id.Value,ManagedDisplayKind.ItemFrame,player); }
            }

            private sealed partial class VirtualDisplay
            {
                public bool TryArmManagedInteraction(PlayerSnapshot player) { AssertOpen(); return provider.ArmDisplay(id.Value,ManagedDisplayKind.WeaponsRack,player); }
                public bool TryTakeManagedTransfer(out SceneManagedDisplayTransferIntent? intent)
                { AssertOpen(); var result = provider.TakeDisplayTransfer(id.Value,ManagedDisplayKind.WeaponsRack); intent = result.Intent; return result.Found; }
                public SceneManagedDisplayTransferResult AcceptManagedTransfer(SceneManagedDisplayTransferIntent intent)
                { AssertOpen(); return provider.AcceptDisplayTransfer(id.Value,ManagedDisplayKind.WeaponsRack,intent); }
                public bool RejectManagedTransfer(SceneManagedDisplayTransferIntent intent) { AssertOpen(); return provider.RejectDisplayTransfer(id.Value,ManagedDisplayKind.WeaponsRack,intent); }
                public bool CancelManagedInteraction(PlayerSnapshot player) { AssertOpen(); return provider.CancelDisplay(id.Value,ManagedDisplayKind.WeaponsRack,player); }
            }
        }
    }
}
