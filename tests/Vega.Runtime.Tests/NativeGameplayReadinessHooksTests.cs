using Multiplicity.Packets;
using NUnit.Framework;
using Vega.Terraria.Hooking;
using Vega.Terraria.Networking;

namespace Vega.Runtime.Tests;

[TestFixture]
public sealed class NativeGameplayReadinessHooksTests
{
    [TestCase(PacketTypes.PlayerSlot)]
    [TestCase(PacketTypes.SyncLoadout)]
    [TestCase(PacketTypes.PlayerHp)]
    [TestCase(PacketTypes.PlayerMana)]
    [TestCase(PacketTypes.PlayerBuff)]
    [TestCase(PacketTypes.NumberOfAnglerQuestsCompleted)]
    public void AuthoritativeCharactersGateSyncAfterWorldRequestUntilReady(PacketTypes packet)
    {
        Assert.That(NativeGameplayReadinessHooks.ShouldApplyPendingCharacterSync(TerrariaConnectionState.ProtocolAccepted, packet), Is.False);
        Assert.That(NativeGameplayReadinessHooks.ShouldApplyPendingCharacterSync(TerrariaConnectionState.WorldRequested, packet), Is.True);
        Assert.That(NativeGameplayReadinessHooks.ShouldApplyPendingCharacterSync(TerrariaConnectionState.FullyConnected, packet), Is.True);
    }

    [TestCase(PacketTypes.ClientSyncedInventory)]
    [TestCase(PacketTypes.PlayerUpdate)]
    [TestCase(PacketTypes.ChatMessage)]
    public void NativeAcknowledgementsMovementAndLoginChatRemainAvailable(PacketTypes packet) =>
        Assert.That(NativeGameplayReadinessHooks.ShouldApplyPendingCharacterSync(TerrariaConnectionState.SendingWorld, packet), Is.False);
    [TestCase(PacketTypes.Tile)]
    [TestCase(PacketTypes.ItemDrop)]
    [TestCase(PacketTypes.ProjectileNew)]
    [TestCase(PacketTypes.ChestItem)]
    [TestCase(PacketTypes.NpcStrike)]
    [TestCase(PacketTypes.PlaceTileEntity)]
    [TestCase(PacketTypes.TogglePvp)]
    [TestCase(PacketTypes.PlaceObject)]
    public void DirectGameplayIsGatedOnlyAfterVanillaWorldEntry(PacketTypes packet)
    {
        Assert.That(NativeGameplayReadinessHooks.ShouldApply(TerrariaConnectionState.ProtocolAccepted, packet), Is.False);
        Assert.That(NativeGameplayReadinessHooks.ShouldApply(TerrariaConnectionState.SendingWorld, packet), Is.False);
        Assert.That(NativeGameplayReadinessHooks.ShouldApply(TerrariaConnectionState.FullyConnected, packet), Is.True);
        Assert.That(NativeGameplayReadinessHooks.ShouldApply((TerrariaConnectionState)11, packet), Is.False);
    }

    [TestCase(PacketTypes.ContinueConnecting2)]
    [TestCase(PacketTypes.PlayerSlot)]
    [TestCase(PacketTypes.ClientSyncedInventory)]
    [TestCase(PacketTypes.PlayerSpawn)]
    [TestCase(PacketTypes.PlayerUpdate)]
    [TestCase(PacketTypes.PlayerHp)]
    [TestCase(PacketTypes.PlayerMana)]
    [TestCase(PacketTypes.SyncLoadout)]
    [TestCase(PacketTypes.SyncPlayerChestIndex)]
    [TestCase(PacketTypes.ChestOpen)]
    [TestCase(PacketTypes.LoadNetModule)]
    [TestCase(PacketTypes.ChatMessage)]
    [TestCase(PacketTypes.RequestSection)]
    [TestCase(PacketTypes.Ping)]
    public void NativeSynchronizationAndChatRemainAvailable(PacketTypes packet) =>
        Assert.That(NativeGameplayReadinessHooks.ShouldApply(TerrariaConnectionState.FullyConnected, packet), Is.False);

    [TestCase("login")]
    [TestCase("AUTH")]
    [TestCase("register")]
    [TestCase("logout")]
    [TestCase("passwd")]
    [TestCase("lang")]
    [TestCase("help")]
    [TestCase("commands")]
    [TestCase("who")]
    [TestCase("online")]
    [TestCase("version")]
    [TestCase("rules")]
    [TestCase("motd")]
    [TestCase("whisper")]
    [TestCase("reply")]
    public void AuthInformationAndChatCommandsRemainAvailable(string command) =>
        Assert.That(NativePlayerCommandHooks.IsAllowedBeforeReadiness(command), Is.True);

    [TestCase("give")]
    [TestCase("warp")]
    [TestCase("worldevent")]
    [TestCase("uploadssc")]
    [TestCase("savessc")]
    public void OtherRegisteredCommandsRequireReadiness(string command) =>
        Assert.That(NativePlayerCommandHooks.IsAllowedBeforeReadiness(command), Is.False);
}
