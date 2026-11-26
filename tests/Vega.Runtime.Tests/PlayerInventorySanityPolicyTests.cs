using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;
using Vega.Terraria.Players;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class PlayerInventorySanityPolicyTests
{
    private static readonly PlayerInventorySanityPolicy Policy = new(() => 350, () => 3, 6000, 100, 9999);

    [Test]
    public void OrdinaryInventoryAndLoadoutSynchronizationRemainValid()
    {
        Assert.That(Process(new PlayerSlot { PlayerId = 7, SlotId = 349, ItemNetId = 1, Stack = 1 }).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(new PlayerSlot { PlayerId = 7, SlotId = 0, ItemNetId = 0, Stack = 0 }).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        Assert.That(Process(new SyncLoadout { PlayerId = 7, LoadoutIndex = 2 }).Kind, Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void InventoryAndLoadoutValidationUsesNativeRegistryBoundsAndSenderIdentity()
    {
        Assert.That(Process(new PlayerSlot { PlayerId = 7, SlotId = 350, ItemNetId = 1, Stack = 1 }).Reason,
            Is.EqualTo("packet.inventory.slot_out_of_range"));
        Assert.That(Process(new PlayerSlot { PlayerId = 7, SlotId = 0, ItemNetId = 6000, Stack = 1 }).Reason,
            Is.EqualTo("packet.inventory.item_type_out_of_range"));
        Assert.That(Process(new PlayerSlot { PlayerId = 7, SlotId = 0, ItemNetId = 1, Stack = 10000 }).Reason,
            Is.EqualTo("packet.inventory.stack_out_of_range"));
        Assert.That(Process(new PlayerSlot { PlayerId = 7, SlotId = 0, ItemNetId = 1, Stack = 1, Prefix = 100 }).Reason,
            Is.EqualTo("packet.inventory.prefix_out_of_range"));
        Assert.That(Process(new SyncLoadout { PlayerId = 7, LoadoutIndex = 3 }).Reason,
            Is.EqualTo("packet.loadout.index_out_of_range"));
        Assert.That(Process(new SyncLoadout { PlayerId = 8, LoadoutIndex = 1 }).Reason,
            Is.EqualTo("packet.player_identity_mismatch"));
        Assert.That(Process(new SyncLoadout { PlayerId = 8, LoadoutIndex = 1 }, PacketDirection.Outbound).Kind,
            Is.EqualTo(PacketDecisionKind.Allow));
    }

    [Test]
    public void PendingServerCharacterHasNoItemsBeforeAccountRestore()
    {
        var pending = TerrariaSandboxSscCharacterAdapter.CreateStarterState(false, includeItems: false);
        var starter = TerrariaSandboxSscCharacterAdapter.CreateStarterState(false, includeItems: true);
        Assert.That(pending.Inventory, Is.Empty);
        Assert.That(pending.Armor, Is.Empty);
        Assert.That(pending.Piggy, Is.Empty);
        Assert.That(pending.Loadouts, Is.Empty);
        Assert.That(pending.Life, Is.EqualTo(100));
        Assert.That(starter.Inventory, Has.Length.EqualTo(3));
        Assert.That(TerrariaSandboxSscCharacterAdapter.CreateStarterState(true, false).Difficulty, Is.EqualTo(3));
    }

    private static PacketProcessingDecision Process(TerrariaPacket packet, PacketDirection direction = PacketDirection.Inbound)
    {
        var view = PacketView.FromPayload(packet.PacketType, packet.ToPayloadArray());
        var context = new PacketProcessingContext<PacketView>(view, direction, new ConnectionId(7), new PlayerId(7), DateTimeOffset.UtcNow);
        return Policy.Process(in context);
    }
}
