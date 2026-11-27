using Vega.Contracts.Scenes;

namespace Vega.PluginSdk;

internal static partial class VegaPluginRuntime
{
    private sealed partial class PluginFakeProvider
    {
        private sealed partial class TileProvider
        {
            private bool OwnsChest(VirtualChestId id, SceneVirtualChestOpenRequest open) => open.Chest.SceneId == Scene.Id &&
                open.Chest.OverlayId == Id && open.Chest.Definition?.Id == id && definition.VirtualChests.Any(chest => chest.Id == id);

            private bool PresentManagedChest(VirtualChestId id, SceneVirtualChestOpenRequest open) => Read(() =>
                OwnsChest(id, open) && Services.Inventory is IVirtualChestGuiHost host && host.TryPresentManagedGui(sceneRuntime, open));

            private (bool Found, SceneVirtualChestGuiTransferIntent? Intent) TakeManagedChestTransfer(VirtualChestId id)
            {
                lock (Owner.sync)
                {
                    AssertAlive();
                    if (sceneRuntime is not IVirtualChestGuiAuthority authority) return (false, null);
                    var chests = definition.VirtualChests;
                    for (int i = 0; i < chests.Count; i++)
                        if (chests[i].Id == id)
                        {
                            bool found = authority.TryTakeManagedGuiTransfer(Scene.Id, Id, id, out var intent);
                            return (found, intent);
                        }
                    return (false, null);
                }
            }

            private SceneVirtualChestTransferResult AcceptManagedChest(VirtualChestId id, SceneVirtualChestGuiTransferIntent intent) => Read(() =>
            {
                if (!OwnsChest(id, intent.Transfer.Open)) return new SceneVirtualChestTransferResult(VirtualChestTransferStatus.StaleInteraction);
                if (Services.Inventory is not IVirtualChestGuiHost host) return new SceneVirtualChestTransferResult(VirtualChestTransferStatus.Unavailable);
                var result = host.AcceptManagedGuiTransfer(sceneRuntime, intent);
                ApplyCommittedChestResult(id, result);
                return result;
            });

            private bool RejectManagedChest(VirtualChestId id, SceneVirtualChestGuiTransferIntent intent) => Read(() =>
                OwnsChest(id, intent.Transfer.Open) && sceneRuntime is IVirtualChestGuiAuthority authority && authority.RejectManagedGuiTransfer(intent));

            private bool CloseManagedChest(VirtualChestId id, SceneSessionObserver observer) => Read(() =>
                sceneRuntime is IVirtualChestGuiAuthority authority && Owner.TryGetEditor(observer.Player, out var player) && player is not null &&
                player.SessionId == observer.Session && authority.RequestManagedGuiClose(player, Scene.Id, Id, id));

            private sealed partial class VirtualChest
            {
                public bool TryPresentManagedGui(SceneVirtualChestOpenRequest open) { AssertOpen(); return provider.PresentManagedChest(id, open); }
                public bool TryTakeManagedGuiTransfer(out SceneVirtualChestGuiTransferIntent? intent)
                {
                    AssertOpen();
                    var result = provider.TakeManagedChestTransfer(id);
                    intent = result.Intent;
                    return result.Found;
                }
                public SceneVirtualChestTransferResult AcceptManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent)
                { AssertOpen(); return provider.AcceptManagedChest(id, intent); }
                public bool RejectManagedGuiTransfer(SceneVirtualChestGuiTransferIntent intent) { AssertOpen(); return provider.RejectManagedChest(id, intent); }
                public bool TryCloseManagedGui(SceneSessionObserver observer) { AssertOpen(); return provider.CloseManagedChest(id, observer); }
            }
        }
    }
}
