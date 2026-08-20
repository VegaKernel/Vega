using NUnit.Framework;
using TShockNG.Contracts.Players;
using Vega.Contracts.Extensions;
using Vega.Core.Extensions;
using Vega.Modules.Pvp;

namespace Vega.Runtime.Tests.Extensions;

[TestFixture]
public sealed class ExtensionStateStoreTests
{
    [Test]
    public void KeysAreStronglyTypedAndSupportTheCompleteScopedLifecycle()
    {
        using var store = new ExtensionStateStore();
        IExtensionStateScope scope = store.CreateScope(new ExtensionStateScopeId("test", "one"));
        var integer = new ExtensionStateKey<int>("shared-name");
        var text = new ExtensionStateKey<string>("shared-name");
        int factoryCalls = 0;

        int created = scope.GetOrCreate(integer, () =>
        {
            factoryCalls++;
            return 10;
        });
        int existing = scope.GetOrCreate(integer, () =>
        {
            factoryCalls++;
            return 20;
        });
        scope.Set(text, "typed");

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.EqualTo(10));
            Assert.That(existing, Is.EqualTo(10));
            Assert.That(factoryCalls, Is.EqualTo(1));
            Assert.That(scope.TryGet(integer, out int number), Is.True);
            Assert.That(number, Is.EqualTo(10));
            Assert.That(scope.TryGet(text, out string? value), Is.True);
            Assert.That(value, Is.EqualTo("typed"));
            Assert.That(scope.Remove(integer), Is.True);
            Assert.That(scope.TryGet(integer, out _), Is.False);
        });

        Assert.That(store.RemoveScope(scope.Id), Is.True);
        Assert.That(() => scope.Set(text, "stale"), Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void RemovingAPlayerSessionDeterministicallyDropsPvpTemporaryState()
    {
        using var store = new ExtensionStateStore();
        var lifecycle = new FakePlayerSessionLifecycle();
        using var pvp = new PvpSessionStateService(store, lifecycle);
        var playerId = new PlayerId(7);
        var firstSession = PlayerSessionId.New();
        DateTimeOffset now = DateTimeOffset.Parse("2026-08-20T12:00:00Z");

        lifecycle.Open(firstSession, playerId, "Alice", now);
        pvp.RecordProjectileWeapon(playerId, 42, 99, now);
        pvp.RecordProjectileWeapon(playerId, 43, 100, now);
        pvp.RecordDeath(playerId, now, TimeSpan.FromSeconds(5));
        pvp.SetSpectating(playerId, true);

        Assert.Multiple(() =>
        {
            Assert.That(pvp.TryGetProjectileWeapon(playerId, 42, out ProjectileWeaponAttribution? attribution), Is.True);
            Assert.That(attribution?.WeaponItemType, Is.EqualTo(99));
            Assert.That(pvp.TryGetProjectileWeapon(playerId, 43, out ProjectileWeaponAttribution? second), Is.True);
            Assert.That(second?.WeaponItemType, Is.EqualTo(100));
            Assert.That(pvp.IsInsideDeathWindow(playerId, now + TimeSpan.FromSeconds(4)), Is.True);
            Assert.That(pvp.IsInsideDeathWindow(playerId, now + TimeSpan.FromSeconds(6)), Is.False);
            Assert.That(pvp.IsSpectating(playerId), Is.True);
        });

        Assert.That(pvp.RemoveProjectileWeapon(playerId, 42), Is.True);
        Assert.That(pvp.TryGetProjectileWeapon(playerId, 42, out _), Is.False);

        lifecycle.Close(firstSession, playerId, "Alice", now + TimeSpan.FromSeconds(7));
        Assert.That(store.TryGetScope(new ExtensionStateScopeId("player-session", firstSession.ToString()), out _), Is.False);

		var secondSession = PlayerSessionId.New();
		lifecycle.Open(secondSession, playerId, "Bob", now + TimeSpan.FromSeconds(8));
		lifecycle.Close(firstSession, playerId, "Alice", now + TimeSpan.FromSeconds(9));

		Assert.Multiple(() =>
		{
			Assert.That(pvp.TryGetProjectileWeapon(playerId, 43, out _), Is.False);
			Assert.That(pvp.IsInsideDeathWindow(playerId, now + TimeSpan.FromSeconds(8)), Is.False);
			Assert.That(pvp.IsSpectating(playerId), Is.False);
			Assert.That(
				store.TryGetScope(new ExtensionStateScopeId("player-session", secondSession.ToString()), out _),
				Is.True);
			Assert.That(secondSession, Is.Not.EqualTo(firstSession));
        });
    }

    private sealed class FakePlayerSessionLifecycle : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;

        public event Action<PlayerSessionClosed>? Closed;

        public void Open(PlayerSessionId sessionId, PlayerId playerId, string name, DateTimeOffset timestamp) =>
            Opened?.Invoke(new PlayerSessionOpened(sessionId, playerId, name, timestamp));

        public void Close(PlayerSessionId sessionId, PlayerId playerId, string name, DateTimeOffset timestamp) =>
            Closed?.Invoke(new PlayerSessionClosed(sessionId, playerId, name, timestamp));
    }
}
