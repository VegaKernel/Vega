using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Items;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Modules.Restrictions;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class ContentRestrictionTests
{
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

    private sealed class Selection : IPlayerItemSelection
    {
        public bool TryGetItemType(PlayerId player, int slot, out int type) { type = 7; return true; }
    }
}
