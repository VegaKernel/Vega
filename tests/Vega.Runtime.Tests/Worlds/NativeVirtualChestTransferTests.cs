using NUnit.Framework;
using Terraria.ID;
using Terraria.Net;
using Terraria.Net.Sockets;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.PluginSdk;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
[NonParallelizable]
public sealed class NativeVirtualChestTransferTests
{
    [Test]
    public async Task PluginChestTransfersNativeItemsOnceAndPreservesBothSidesOnRejection()
    {
        const int slot = 3;
        var world = new WorldId("virtual-chest-native");
        var dispatcher = new InlineDispatcher();
        var scenes = new SceneRuntime();
        using var boundary = new TerrariaPlayerBoundary(dispatcher, new TerrariaConnectionMetadataRegistry(), world);
        var inventory = new TerrariaPlayerInventoryService(dispatcher, boundary);
        var oldPlayer = global::Terraria.Main.player[slot];
        var oldClients = (global::Terraria.RemoteClient[])global::Terraria.Netplay.Clients.Clone();
        int oldMode = global::Terraria.Main.netMode;
        bool oldDedicated = global::Terraria.Main.dedServ;
        bool oldSsc = global::Terraria.Main.ServerSideCharacter;
        var oldBuffers = (global::Terraria.MessageBuffer[])global::Terraria.NetMessage.buffer.Clone();
        try
        {
            var player = new global::Terraria.Player { active = true, whoAmI = slot, name = "Buyer", position = new(160, 160) };
            global::Terraria.Main.player[slot] = player;
            global::Terraria.Main.dedServ = true;
            global::Terraria.Main.netMode = 2;
            for (int i = 0; i < global::Terraria.NetMessage.buffer.Length; i++)
                global::Terraria.NetMessage.buffer[i] = new global::Terraria.MessageBuffer { whoAmI = i, broadcast = i == slot };
            for (int i = 0; i < global::Terraria.Netplay.Clients.Length; i++)
                global::Terraria.Netplay.Clients[i] = new global::Terraria.RemoteClient { Id = i };
            var client = new global::Terraria.RemoteClient { Id = slot, State = 10 };
            global::Terraria.Netplay.Clients[slot] = client;
            var socket = new RecordingSocket();
            client.Activate(socket);
            client.State = 10;
            boundary.Attach();
            boundary.Opened += opened => scenes.OpenSession(world, opened);
            boundary.Closed += closed => scenes.CloseSession(world, closed);
            TerrariaRuntime.Hooks.Netplay.NotifyServerPlayerEnteringWorld(slot);
            Assert.That(boundary.TryGet(new PlayerId(slot), out var snapshot), Is.True);
            Assert.That(snapshot!.SessionId, Is.Not.Null);
            // Native server output is counted by an in-process recording socket.

            VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(world, null, null, null, null, scenes, inventory));
            await using var scope = new HotModuleRegistrationScope(new NullRegistry(), new QuietLogger(), "Plugin.NativeChest", Path.GetTempPath(), playerDirectory: boundary);
            var fake = ((IVegaPluginContext)scope).World.FakeProvider;
            Assert.That(fake.SupportsVirtualChestTransfers, Is.True);
            using var provider = fake.CreatePersonal("Reward", new(10, 10, 2, 2),
                SceneTileArea.Fill(2, 2, new SceneTile(21, Flags: SceneTileFlags.Active)), [SceneSessionObserver.From(snapshot)]);
            using var chest = provider.CreateVirtualChest(0, 0, "Reward", [new(0, ItemID.StoneBlock, 10, 0)]);

            SceneVirtualChestOpenRequest Open()
            {
                Assert.That(scenes.TryRequestVirtualChestOpen(snapshot, 10, 10), Is.True);
                Assert.That(chest.TryTakeOpenRequest(out var request), Is.True);
                return request!.Value;
            }

            var initial = await inventory.CaptureAsync(snapshot);
            Assert.That(initial, Is.Not.Null);
            var first = new SceneVirtualChestTransferRequest(Open(), initial!.Revision, 0, 0, 4, VirtualChestTransferDirection.Withdraw);
            global::Terraria.Main.ServerSideCharacter = false;
            Assert.That(chest.Transfer(first).Status, Is.EqualTo(VirtualChestTransferStatus.AuthorityRequired));
            global::Terraria.Main.ServerSideCharacter = true;
            Assert.That(chest.Transfer(first with { Open = first.Open with { RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-1) } }).Status,
                Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));
            var withdrawn = chest.Transfer(first);
            Assert.That(withdrawn.Status, Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(withdrawn.Error, Is.Null);
            Assert.That(socket.LastSend, Has.Length.GreaterThanOrEqualTo(3));
            Assert.That(socket.LastSend[2], Is.EqualTo(MessageID.SyncEquipment));
            Assert.That(chest.Definition.Revision, Is.EqualTo(2));
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(6));
            Assert.That(player.inventory[0].type, Is.EqualTo(ItemID.StoneBlock));
            Assert.That(player.inventory[0].stack, Is.EqualTo(4));
            Assert.That(chest.Transfer(first).Status, Is.EqualTo(VirtualChestTransferStatus.RevisionConflict));
            Assert.That(chest.Transfer(first with { InventoryRevision = withdrawn.InventoryRevision }).Status, Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));

            var deposit = new SceneVirtualChestTransferRequest(Open(), withdrawn.InventoryRevision, 0, 0, 2, VirtualChestTransferDirection.Deposit);
            player.position = new(2000, 2000);
            Assert.That(chest.Transfer(deposit).Status, Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));
            player.position = new(160, 160);
            using (var cover = scenes.CreateScene(world, "cover", "test"))
            {
                cover.AddOverlay(new(default, "cover", new(10, 10, 1, 1), 5, SceneObserverScope.Global, SceneTileArea.Fill(1, 1, SceneTile.Air)));
                Assert.That(chest.Transfer(deposit).Status, Is.EqualTo(VirtualChestTransferStatus.StaleInteraction));
            }
            Assert.That(chest.Transfer(deposit with { Amount = 99 }).Status, Is.EqualTo(VirtualChestTransferStatus.InvalidItem));
            var deposited = chest.Transfer(deposit);
            Assert.That(deposited.Status, Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(8));
            Assert.That(player.inventory[0].stack, Is.EqualTo(2));
            var final = new SceneVirtualChestTransferRequest(Open(), deposited.InventoryRevision, 0, 0, 2, VirtualChestTransferDirection.Deposit);
            Assert.That(chest.Transfer(final).Status, Is.EqualTo(VirtualChestTransferStatus.Applied));
            Assert.That(chest.Definition.Items.Single().Stack, Is.EqualTo(10));
            Assert.That(player.inventory[0].IsAir, Is.True);
            provider.Move(1, 0); // A later provider edit must not restore stale chest contents.
            Assert.That(scenes.TryResolveVirtualChest(world, SceneSessionObserver.From(snapshot), 11, 10, out var resolved), Is.True);
            Assert.That(resolved!.Value.Definition.Revision, Is.EqualTo(4));
            Assert.That(resolved.Value.Definition.Items.Single().Stack, Is.EqualTo(10));

            // Editable signs retain the updated revision through subsequent SDK edits and movement.
            Assert.That(fake.SupportsVirtualSignEdits, Is.True);
            using var signProvider = fake.CreatePersonal("Sign", new(10, 13, 2, 2),
                SceneTileArea.Fill(2, 2, new SceneTile(55, Flags: SceneTileFlags.Active)), [SceneSessionObserver.From(snapshot)]);
            using var sign = signProvider.CreateVirtualSign(0, 0, "Old", allowEdits: true);
            Assert.That(scenes.TryRequestVirtualSignRead(snapshot, 10, 13), Is.True);
            Assert.That(scenes.TryRequestVirtualSignEdit(snapshot, 10, 13, "New"), Is.True);
            Assert.That(sign.TryTakeEditRequest(out var edit), Is.True);
            Assert.That(sign.TryAcceptEdit(edit!.Value), Is.True);
            Assert.That(sign.Definition.Text, Is.EqualTo("New"));
            Assert.That(sign.TrySetText(2, "Updated"), Is.True);
            Assert.That(sign.Definition.AllowEdits, Is.True);
            signProvider.Move(1, 0);
            Assert.That(scenes.TryResolveVirtualSign(world, SceneSessionObserver.From(snapshot), 11, 13, out var signResolved), Is.True);
            Assert.That(signResolved!.Value.Definition.Text, Is.EqualTo("Updated"));
        }
        finally
        {
            VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty);
            global::Terraria.Main.player[slot] = oldPlayer;
            Array.Copy(oldClients, global::Terraria.Netplay.Clients, oldClients.Length);
            global::Terraria.Main.netMode = oldMode;
            global::Terraria.Main.dedServ = oldDedicated;
            global::Terraria.Main.ServerSideCharacter = oldSsc;
            Array.Copy(oldBuffers, global::Terraria.NetMessage.buffer, oldBuffers.Length);
        }
    }

    private sealed class InlineDispatcher : IGameThreadDispatcher
    {
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action) { action(); return true; }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
    }

    private sealed class RecordingSocket : ISocket
    {
        public byte[] LastSend { get; private set; } = [];
        public void Close() { }
        public bool IsConnected() => true;
        public bool IsDataAvailable() => false;
        public RemoteAddress GetRemoteAddress() => throw new NotSupportedException();
        public void Connect(RemoteAddress remoteAddress) => throw new NotSupportedException();
        public bool StartListening(SocketConnectionAccepted callback) => throw new NotSupportedException();
        public void StopListening() => throw new NotSupportedException();
        public void AsyncSend(byte[] data, int offset, int size, SocketSendCallback callback, object? state = null)
        {
            LastSend = data.AsSpan(offset, size).ToArray();
            callback(state);
        }
        public void AsyncReceive(byte[] data, int offset, int size, SocketReceiveCallback callback, object? state = null) => throw new NotSupportedException();
    }

    private sealed class QuietLogger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
    private sealed class NullRegistry : ICommandRegistry
    {
        public IDisposable Register(ICommandHandler handler) => throw new NotSupportedException();
    }
}
