using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Microsoft.Data.Sqlite;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Modules.Restrictions;
using Vega.Persistence.Sqlite;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class ContentRestrictionTests
{
    [Test]
    public async Task NamedRestrictionsAndPerContentGroupRulesAreDurableAndDelegationChecked()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-restrictions", Guid.NewGuid().ToString("N"));
        try
        {
            var db = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db")));
            Assert.That((await db.Authorization.CreateGroupAsync("builders", "default", "server")).Succeeded, Is.True);
            var store = new RestrictionStore(Path.Combine(root, "restrictions.json"), 100, 100, 100);
            var cmd = new RestrictionCommand(store, RestrictedContentKind.Item, name => name == "Torch" ? 8 : null, db.Authorization);
            CommandInvocation request = new("itemban", ["add", "Torch"], new Principal(true), new Output());
            Assert.That((await cmd.ExecuteAsync(request, default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(await store.SetAsync(RestrictedContentKind.Item, 9, true), Is.True);
            var policy = new ContentRestrictionPolicy(store, new GroupAuthorization(db.Authorization), new Selection());
            Assert.That(policy.IsAllowed(new PlayerId(1), RestrictedContentKind.Item, 8), Is.False);
            var allowance = request with { Arguments = ["allow", "Torch", "builders"] };
            Assert.That((await cmd.ExecuteAsync(allowance with { Principal = new Principal(false) }, default)).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That((await cmd.ExecuteAsync(allowance, default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(policy.IsAllowed(new PlayerId(1), RestrictedContentKind.Item, 8), Is.True);
            Assert.That(policy.IsAllowed(new PlayerId(1), RestrictedContentKind.Item, 9), Is.False);
            Assert.That(policy.IsAllowed(new PlayerId(2), RestrictedContentKind.Item, 8), Is.False);
            var reopened = await SqliteVegaDatabase.OpenAsync(new SqliteVegaDatabaseOptions(Path.Combine(root, "vega.db")));
            Assert.That(reopened.Authorization.HasPermission("builders", "vega.restrictions.allow.item.8"), Is.True);
            Assert.That((await cmd.ExecuteAsync(request with { Arguments = ["disallow", "Torch", "builders"] }, default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(policy.IsAllowed(new PlayerId(1), RestrictedContentKind.Item, 8), Is.False);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void NativeSymbolicContentAndPrefixNamesResolveWithoutAmbiguity()
    {
        Assert.That(TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Item, "Copper Shortsword"), Is.EqualTo(global::Terraria.ID.ItemID.CopperShortsword));
        Assert.That(TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Tile, "Dirt"), Is.EqualTo(global::Terraria.ID.TileID.Dirt));
        Assert.That(TerrariaReferenceDataExporter.ResolvePrefixName("legendary"), Is.EqualTo(global::Terraria.ID.PrefixID.Legendary));
        Assert.That(TerrariaReferenceDataExporter.ResolvePrefixName("unknown"), Is.Null);
    }

    [Test]
    public async Task RestrictedItemUseIsRejectedUnlessPermittedAndStoreSurvivesReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-restrictions", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "restrictions.json");
        try
        {
            var store = new RestrictionStore(path, 100, 100, 100);
            Assert.That(await store.SetAsync(RestrictedContentKind.Item, 7, true), Is.True);
            Assert.That(await store.SetAsync(RestrictedContentKind.Tile, 0, true), Is.True);
            Assert.That(await store.SetAsync(RestrictedContentKind.Projectile, 100, true), Is.False);
            var reopened = new RestrictionStore(path, 100, 100, 100);
            var auth = new Authorization();
            var policy = new ContentRestrictionPolicy(reopened, auth, new Selection());
            Assert.That(Process(policy, useItem: false).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(Process(policy, useItem: true).Reason, Is.EqualTo("restriction.item_use"));
            auth.Bypass = true;
            Assert.That(Process(policy, useItem: true).Kind, Is.EqualTo(PacketDecisionKind.Allow));
            Assert.That(reopened.List(RestrictedContentKind.Tile), Is.EqualTo(new[] { 0 }));
            Assert.That(await reopened.SetAsync(RestrictedContentKind.Item, 7, false), Is.True);
            auth.Bypass = false;
            Assert.That(Process(policy, useItem: true).Kind, Is.EqualTo(PacketDecisionKind.Allow));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static PacketProcessingDecision Process(ContentRestrictionPolicy policy, bool useItem)
    {
        var packet = new PlayerUpdate { PlayerId = 1, SelectedItem = 0, ControlFlags = useItem ? UpdatePlayerControlFlags.ControlUseItem : UpdatePlayerControlFlags.None };
        using var stream = new MemoryStream();
        packet.ToStream(stream, includeHeader: false);
        var view = PacketView.FromPayload(PacketTypes.PlayerUpdate, stream.ToArray());
        var context = new PacketProcessingContext<PacketView>(view, PacketDirection.Inbound, new ConnectionId(1), new PlayerId(1), DateTimeOffset.UtcNow);
        return policy.Process(in context);
    }

    private sealed class Authorization : IPlayerAuthorizationService
    {
        public bool Bypass { get; set; }
        public bool HasPermission(PlayerId playerId, string permission) => Bypass;
    }

    private sealed class GroupAuthorization(IAuthorizationService groups) : IPlayerAuthorizationService
    {
        public bool HasPermission(PlayerId playerId, string permission) => groups.HasPermission(playerId.Slot == 1 ? "builders" : "default", permission);
    }
    private sealed class Principal(bool server) : ICommandPrincipal
    {
        public string Name => server ? "Server" : "RestrictionManager";
        public bool IsServer => server;
        public bool HasPermission(string permission) => server || permission == "vega.restrictions.manage";
    }
    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class Selection : IPlayerItemSelection
    {
        public bool TryGetItemType(PlayerId player, int slot, out int type) { type = 7; return true; }
    }
}
