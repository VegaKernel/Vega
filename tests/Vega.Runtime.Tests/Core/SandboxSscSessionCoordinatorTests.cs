using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Sandboxes;
using Vega.Terraria.Players;
using System.Text.Json;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class SandboxSscSessionCoordinatorTests
{
    [Test]
    public async Task ActorTransactionCheckpointPersistsExactAuthenticatedInventoryAndRejectsOldTenure()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-actor-ssc-checkpoint", Guid.NewGuid().ToString("N"));
        var player = Player(new PlayerId(7), PlayerSessionId.New(), null, WorldId.Primary);
        var binding = new PlayerAccountBinding(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid());
        try
        {
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var accounts = new FakeAccounts();
            accounts.SetBinding(binding);
            var characters = new FakeCharacters();
            var logger = new RecordingLogger();
            using var coordinator = new SandboxSscSessionCoordinator(new FakePlayers(player), new FakeLifecycle(), accounts,
                new FakeOperations(), store, characters, WorldId.Primary, SandboxSscMode.Inherit, logger, primaryCharacterStore: store);
            await EventuallyAsync(() => logger.PrimaryRestores == 1 && coordinator.IsCharacterReady(player));
            characters.CapturePayload = "actor-reward-checkpoint";
            Assert.That(await coordinator.CheckpointActorTransactionAsync(player, binding), Is.True);
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("actor-reward-checkpoint"));
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(43), Guid.NewGuid()));
            characters.CapturePayload = "replacement-auth-inventory";
            Assert.That(await coordinator.CheckpointActorTransactionAsync(player, binding), Is.False);
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("actor-reward-checkpoint"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(true, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task TrustedTransferOnlyPersistsCharacterCapturedBeforeReset(bool preReset, bool close)
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-session", Guid.NewGuid().ToString("N"));
        string storePath = Path.Combine(root, "ssc.json");
        var worldId = new WorldId("arena");
        var playerId = new PlayerId(7);
        var sessionId = PlayerSessionId.New();
        try
        {
            var store = new SandboxSscStore(storePath);
            SandboxSscWriteResult seeded = store.Save(
                "42", worldId, SandboxSscMode.Isolated, "target-v1", 0, DateTimeOffset.UtcNow);
            Assert.That(seeded.IsSuccess, Is.True);

            var directory = new FakePlayers(Player(playerId, sessionId, "42", worldId));
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            var operations = new FakeOperations();
            var characters = new FakeCharacters();
            using var coordinator = new SandboxSscSessionCoordinator(
                directory,
                lifecycle,
                accounts,
                operations,
                store,
                characters,
                worldId,
                SandboxSscMode.Isolated,
                new RecordingLogger());

            await EventuallyAsync(() => characters.AppliedPayload == "target-v1" && accounts.RestoredAccountId == "42");
            Assert.That(operations.KickCount, Is.Zero);

            characters.CapturePayload = "target-v2";
            if (preReset) lifecycle.BeginClose(sessionId, playerId);
            directory.Players = [];
            characters.CapturePayload = "different-character";
            if (close) lifecycle.Close(sessionId, playerId);
            else coordinator.Dispose();
            if (preReset)
                await EventuallyAsync(() => store.Read("42", worldId, SandboxSscMode.Isolated)?.Payload == "target-v2");
            else
            {
                await Task.Delay(350);
                Assert.That(store.Read("42", worldId, SandboxSscMode.Isolated)?.Payload, Is.EqualTo("target-v1"));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task MissingTargetStateFailsClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-sandbox-ssc-session-missing", Guid.NewGuid().ToString("N"));
        var worldId = new WorldId("arena");
        var playerId = new PlayerId(3);
        var sessionId = PlayerSessionId.New();
        try
        {
            var directory = new FakePlayers(Player(playerId, sessionId, "42", worldId));
            var lifecycle = new FakeLifecycle();
            var operations = new FakeOperations();
            using var coordinator = new SandboxSscSessionCoordinator(
                directory,
                lifecycle,
                new FakeAccounts(),
                operations,
                new SandboxSscStore(Path.Combine(root, "ssc.json")),
                new FakeCharacters(),
                worldId,
                SandboxSscMode.Isolated,
                new RecordingLogger());

            await EventuallyAsync(() => operations.KickCount == 1);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestCase(42, true)]
    [TestCase(43, false)]
    public async Task NumericTrustedTransferStopsCheckpointingAfterAuthenticationTenureChanges(int nextAccount, bool explicitLogout)
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-transferred-logout", Guid.NewGuid().ToString("N"));
        var worldId = new WorldId("arena");
        var playerId = new PlayerId(7);
        var sessionId = PlayerSessionId.New();
        try
        {
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            Assert.That(store.Save("42", worldId, SandboxSscMode.Isolated, "transferred", 0, DateTimeOffset.UtcNow).IsSuccess, Is.True);
            var directory = new FakePlayers(Player(playerId, sessionId, "42", worldId));
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            var characters = new FakeCharacters();
            var logger = new RecordingLogger();
            var time = new OffsetTimeProvider();
            var operations = new FakeOperations();
            using var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, operations, store,
                characters, worldId, SandboxSscMode.Isolated, logger, time);
            await EventuallyAsync(() => logger.TransferRestores == 1);
            characters.CapturePayload = "before-logout";
            if (explicitLogout) accounts.Logout();
            accounts.SetBinding(new(playerId, sessionId, new AccountId(nextAccount), Guid.NewGuid()));
            characters.CapturePayload = "new-auth-character";
            time.Advance(TimeSpan.FromSeconds(6));
            if (explicitLogout)
                await EventuallyAsync(() => store.Read("42", worldId, SandboxSscMode.Isolated)?.Payload == "before-logout");
            await Task.Delay(550);
            Assert.That(accounts.TransferEstablishCount, Is.EqualTo(1));
            Assert.That(logger.TransferRestores, Is.EqualTo(1));
            Assert.That(operations.KickCount, Is.Zero);
            lifecycle.BeginClose(sessionId, playerId);
            lifecycle.Close(sessionId, playerId);
            coordinator.Dispose();
            Assert.That(store.Read("42", worldId, SandboxSscMode.Isolated)?.Payload, Is.EqualTo(explicitLogout ? "before-logout" : "transferred"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task LegacyGuestTransferStillUsesNativeSessionCapture()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-guest-transfer", Guid.NewGuid().ToString("N"));
        var worldId = new WorldId("arena");
        var playerId = new PlayerId(7);
        var sessionId = PlayerSessionId.New();
        try
        {
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            const string guest = "guest:alice";
            Assert.That(store.Save(guest, worldId, SandboxSscMode.Isolated, "guest-state", 0, DateTimeOffset.UtcNow).IsSuccess, Is.True);
            var directory = new FakePlayers(Player(playerId, sessionId, guest, worldId));
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            var characters = new FakeCharacters();
            var logger = new RecordingLogger();
            using var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), store,
                characters, worldId, SandboxSscMode.Isolated, logger);
            await EventuallyAsync(() => logger.TransferRestores == 1);
            Assert.That(characters.AppliedPayload, Is.EqualTo("guest-state"));
            Assert.That(accounts.TransferEstablishCount, Is.Zero);
            var connected = directory.Players[0] with
            {
                Connection = directory.Players[0].Connection! with { LoginPhase = ConnectionLoginPhase.FullyConnected }
            };
            directory.Players = [connected];
            Assert.That(coordinator.IsCharacterReady(connected), Is.True);
            Assert.That(coordinator.IsGameplayAllowed(connected, false), Is.False);
            Assert.That(coordinator.IsCharacterReady(connected with { SessionId = PlayerSessionId.New() }), Is.False);
            directory.Players = [connected with { Connection = connected.Connection! with { TrustedTransferAccountId = "other-guest" } }];
            Assert.That(coordinator.IsCharacterReady(directory.Players[0]), Is.False);
            directory.Players = [connected];
            characters.CapturePayload = "guest-final";
            lifecycle.BeginClose(sessionId, playerId);
            lifecycle.Close(sessionId, playerId);
            coordinator.Dispose();
            Assert.That(store.Read(guest, worldId, SandboxSscMode.Isolated)?.Payload, Is.EqualTo("guest-final"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void StarterCharacterIsServerOwnedAndDeterministic()
    {
        string payload = TerrariaSandboxSscCharacterAdapter.CreateStarterPayload(false);
        var state = JsonSerializer.Deserialize<TerrariaSandboxSscCharacterAdapter.CharacterState>(payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.That(payload, Is.EqualTo(TerrariaSandboxSscCharacterAdapter.CreateStarterPayload(false)));
        Assert.That(state.Life, Is.EqualTo(100));
        Assert.That(state.MaxMana, Is.EqualTo(20));
        Assert.That(state.Inventory.Length, Is.EqualTo(3));
        Assert.That(state.Armor, Is.Empty);
        Assert.That(state.Piggy, Is.Empty);
        Assert.That(state.Loadouts, Is.Empty);
        Assert.That(state.SpawnX, Is.EqualTo(-1));
    }

    [Test]
    public async Task PrimaryPersistenceUsesStarterCheckpointAndNewAuthTenureWithoutImportingLegacyState()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-primary-characters", Guid.NewGuid().ToString("N"));
        var playerId = new PlayerId(7);
        var sessionId = PlayerSessionId.New();
        PlayerSnapshot player = Player(playerId, sessionId, null, WorldId.Primary);
        try
        {
            var legacy = new SandboxSscStore(Path.Combine(root, "legacy.json"));
            var store = new SandboxSscStore(Path.Combine(root, "primary.json"));
            Assert.That(legacy.Save("42", WorldId.Primary, SandboxSscMode.Inherit, "legacy-client", 0, DateTimeOffset.UtcNow).IsSuccess, Is.True);
            var directory = new FakePlayers(player);
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            accounts.SetBinding(new(playerId, sessionId, new AccountId(42), Guid.NewGuid()));
            var characters = new FakeCharacters { CapturePayload = "different-client-character" };
            var logger = new RecordingLogger();
            var time = new OffsetTimeProvider();
            using var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), legacy,
                characters, WorldId.Primary, SandboxSscMode.Inherit, logger, time, primaryCharacterStore: store);
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            Assert.That(characters.AppliedPayload, Is.EqualTo("server-starter"));
            Assert.That(characters.InitialCount, Is.EqualTo(1));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("server-starter"));
            characters.CapturePayload = "account42-checkpoint";
            time.Advance(TimeSpan.FromSeconds(6));
            await EventuallyAsync(() => store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload == "account42-checkpoint");

            // No logout polling gap is required: a new account epoch on the same
            // native socket must initialize independently and cannot inherit old state.
            accounts.SetBinding(new(playerId, sessionId, new AccountId(43), Guid.NewGuid()));
            characters.CapturePayload = "retired-account-character";
            await EventuallyAsync(() => logger.PrimaryRestores == 2);
            Assert.That(characters.AppliedPayload, Is.EqualTo("server-starter"));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("account42-checkpoint"));
            characters.CapturePayload = "account43-final";
            lifecycle.BeginClose(sessionId, playerId);
            directory.Players = [];
            accounts.SetBinding(null);
            lifecycle.Close(sessionId, playerId);
            await EventuallyAsync(() => store.Read("43", WorldId.Primary, SandboxSscMode.Inherit)?.Payload == "account43-final");

            var replacement = Player(playerId, PlayerSessionId.New(), null, WorldId.Primary);
            directory.Players = [replacement];
            accounts.SetBinding(new(playerId, replacement.SessionId!.Value, new AccountId(43), Guid.NewGuid()));
            await EventuallyAsync(() => logger.PrimaryRestores == 3);
            Assert.That(characters.AppliedPayload, Is.EqualTo("account43-final"));
            Assert.That(characters.InitialCount, Is.EqualTo(2));
            Assert.That(legacy.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("legacy-client"));
            // External revision wins even when the old character is captured before reset.
            var existing = store.Read("43", WorldId.Primary, SandboxSscMode.Inherit)!;
            Assert.That(store.Save("43", WorldId.Primary, SandboxSscMode.Inherit, "external-newer", existing.Revision, DateTimeOffset.UtcNow).IsSuccess, Is.True);
            characters.CapturePayload = "older";
            lifecycle.BeginClose(replacement.SessionId!.Value, playerId);
            coordinator.Dispose();
            Assert.That(store.Read("43", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("external-newer"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task PrimaryPersistenceIsDisabledByDefaultAndCannotEnrollGuests()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-primary-disabled", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var directory = new FakePlayers(player);
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            var characters = new FakeCharacters();
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            Assert.That(PrimaryCharacterPersistenceOptions.Load(Path.Combine(root, "not-present.json")).Enabled, Is.False);
            using (var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, new RecordingLogger()))
                await Task.Delay(450);
            Assert.That(characters.InitialCount, Is.Zero);
            accounts.SetBinding(null);
            using (var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, new RecordingLogger(), primaryCharacterStore: store))
                await Task.Delay(450);
            Assert.That(characters.InitialCount, Is.Zero);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task LogoutCapturesOnlySynchronouslyBeforeBindingIsRevoked(bool synchronous)
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-primary-logout", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var directory = new FakePlayers(player);
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            var characters = new FakeCharacters();
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var logger = new RecordingLogger();
            using var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, logger, primaryCharacterStore: store);
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            characters.CapturePayload = "logout-final";
            characters.DeferCapture = !synchronous;
            accounts.Logout();
            characters.CapturePayload = "unbound-character";
            if (synchronous)
                await EventuallyAsync(() => store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload == "logout-final");
            else
            {
                await EventuallyAsync(() => characters.DeferredCancelled);
                Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("server-starter"));
            }
            characters.DeferCapture = false;
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => logger.PrimaryRestores == 2);
            Assert.That(characters.AppliedPayload, Is.EqualTo(synchronous ? "logout-final" : "server-starter"));
            Assert.That(characters.InitialCount, Is.EqualTo(1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task ExplicitSscCommandsRequireCurrentAuthorityAndConfirmationAndUseOnlyInitialJoinSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-ssc-commands", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var guest = Player(new(8), PlayerSessionId.New(), null, WorldId.Primary) with { Name = "guest" };
            var directory = new FakePlayers(player, guest);
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            var characters = new FakeCharacters { CapturePayload = "client-at-entry", EarlyJoinPayload = "client-before-world-data" };
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var logger = new RecordingLogger();
            var time = new OffsetTimeProvider();
            using var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, logger, time, primaryCharacterStore: store);
            lifecycle.Enter(player);
            characters.CapturePayload = "client-after-entry";
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("server-starter"));

            var principal = new CommandPlayer(player, ["vega.ssc.upload", "vega.ssc.save", "vega.ssc.override"]);
            CommandInvocation Invoke(string name, params string[] args) => new(name, args, principal, new Output());
            var upload = new UploadSscCommand(coordinator, directory);
            var save = new SaveSscCommand(coordinator);
            var current = new OverrideSscCommand(coordinator, directory);
            Assert.That((await upload.ExecuteAsync(Invoke("uploadssc"), default)).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
            Assert.That((await upload.ExecuteAsync(Invoke("uploadssc", "guest", "confirm"), default)).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            Assert.That((await current.ExecuteAsync(Invoke("overridessc", "guest"), default)).ErrorCode, Is.EqualTo("ssc.authenticated_session_not_ready"));
            Assert.That((await current.ExecuteAsync(Invoke("overridessc", " "), default)).ErrorCode, Is.EqualTo("ssc.player_not_unique"));
            Assert.That((await upload.ExecuteAsync(Invoke("uploadssc", "confirm"), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(characters.AppliedPayload, Is.EqualTo("client-before-world-data"));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("client-before-world-data"));

            characters.CapturePayload = "current-state";
            Assert.That((await current.ExecuteAsync(Invoke("ossc", "7"), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("current-state"));
            characters.CapturePayload = "saved-all";
            Assert.That((await save.ExecuteAsync(Invoke("savessc"), default)).Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("saved-all"));

            var stale = new CommandPlayer(player with { SessionId = PlayerSessionId.New() }, ["vega.ssc.override"]);
            Assert.That((await current.ExecuteAsync(new("ossc", ["7"], stale, new Output()), default)).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            characters.DeferCapture = true;
            characters.CapturePayload = "must-not-save";
            var pending = current.ExecuteAsync(Invoke("ossc", "7"), default).AsTask();
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(43), Guid.NewGuid()));
            Assert.That((await pending).Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("saved-all"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task JoinImportPreservesConcurrentRevisionAndDoesNotCheckpointFailedLiveApply(bool conflict)
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-ssc-import", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var directory = new FakePlayers(player);
            var lifecycle = new FakeLifecycle();
            var accounts = new FakeAccounts();
            var characters = new FakeCharacters { CapturePayload = "join-data" };
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var logger = new RecordingLogger();
            var time = new OffsetTimeProvider();
            using var coordinator = new SandboxSscSessionCoordinator(directory, lifecycle, accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, logger, time, primaryCharacterStore: store);
            lifecycle.Enter(player);
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            if (conflict)
            {
                var original = store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)!;
                Assert.That(store.Save("42", WorldId.Primary, SandboxSscMode.Inherit, "newer-external", original.Revision, time.GetUtcNow()).IsSuccess, Is.True);
            }
            else characters.FailAuthenticatedApply = true;
            var command = new UploadSscCommand(coordinator, directory);
            var principal = new CommandPlayer(player, ["vega.ssc.upload"]);
            var result = await command.ExecuteAsync(new("uploadssc", ["confirm"], principal, new Output()), default);
            Assert.That(result.ErrorCode, Is.EqualTo(conflict ? "sandbox.ssc.revision_conflict" : "ssc.import_saved_apply_failed"));
            Assert.That(characters.AppliedPayload, Is.EqualTo("server-starter"));
            characters.CapturePayload = "old-live-state";
            time.Advance(TimeSpan.FromSeconds(6));
            await Task.Delay(450);
            lifecycle.BeginClose(player.SessionId!.Value, player.Id);
            coordinator.Dispose();
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo(conflict ? "newer-external" : "join-data"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task UploadDoesNotSubstituteCurrentStateWhenNativeJoinSnapshotIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-ssc-missing-join", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var directory = new FakePlayers(player);
            var accounts = new FakeAccounts();
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            var characters = new FakeCharacters { CapturePayload = "not-join-data" };
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var logger = new RecordingLogger();
            using var coordinator = new SandboxSscSessionCoordinator(directory, new FakeLifecycle(), accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, logger, primaryCharacterStore: store);
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            var command = new UploadSscCommand(coordinator, directory);
            var result = await command.ExecuteAsync(new("uploadssc", ["confirm"], new CommandPlayer(player, ["vega.ssc.upload"]), new Output()), default);
            Assert.That(result.ErrorCode, Is.EqualTo("ssc.join_snapshot_unavailable"));
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("server-starter"));
            coordinator.Dispose();
            var save = new OverrideSscCommand(coordinator, directory);
            Assert.That((await save.ExecuteAsync(new("overridessc", ["alice"], new CommandPlayer(player, ["vega.ssc.override"]), new Output()), default)).ErrorCode,
                Is.EqualTo("ssc.authenticated_session_not_ready"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task ReadinessRequiresExactAuthTenureAndOptionallyCompletedRestore()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-primary-ready", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var directory = new FakePlayers(player);
            var accounts = new FakeAccounts();
            var characters = new FakeCharacters { ApplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var logger = new RecordingLogger();
            using var coordinator = new SandboxSscSessionCoordinator(directory, new FakeLifecycle(), accounts, new FakeOperations(), store,
                characters, WorldId.Primary, SandboxSscMode.Inherit, logger, primaryCharacterStore: store);
            Assert.That(coordinator.IsGameplayAllowed(player, false), Is.False);
            Assert.That(coordinator.IsGameplayAllowed(player, true), Is.False);
            Assert.That(coordinator.GetCharacterReadiness(player), Is.EqualTo(SscCharacterReadiness.Unauthenticated));
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            Assert.That(coordinator.IsGameplayAllowed(player, false), Is.True);
            await EventuallyAsync(() => characters.ApplyStarted);
            Assert.That(coordinator.IsGameplayAllowed(player, true), Is.False);
            Assert.That(coordinator.GetCharacterReadiness(player), Is.EqualTo(SscCharacterReadiness.Restoring));
            characters.ApplyRelease!.SetResult();
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            Assert.That(coordinator.IsGameplayAllowed(player, true), Is.True);
            Assert.That(coordinator.IsCharacterReady(player), Is.True);
            Assert.That(coordinator.IsGameplayAllowed(player with { SessionId = PlayerSessionId.New() }, false), Is.False);
            Assert.That(coordinator.IsGameplayAllowed(player with { WorldId = new("elsewhere") }, true), Is.False);
            Assert.That(coordinator.IsGameplayAllowed(player with { Connection = player.Connection! with { LoginPhase = ConnectionLoginPhase.SendingWorld } }, false), Is.False);

            characters.ApplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            Assert.That(coordinator.IsGameplayAllowed(player, false), Is.True);
            Assert.That(coordinator.IsGameplayAllowed(player, true), Is.False);
            accounts.Logout();
            Assert.That(coordinator.IsGameplayAllowed(player, false), Is.False);
            characters.ApplyRelease!.TrySetResult();
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task AuthenticationNotificationRestoresWithoutPollingAndLogoutCancelsOldRestore()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-ssc-auth-notification", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var accounts = new FakeAccounts();
            var previousApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var characters = new FakeCharacters { ApplyRelease = previousApply };
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            var logger = new RecordingLogger();
            using var coordinator = new SandboxSscSessionCoordinator(new FakePlayers(player), new FakeLifecycle(), accounts,
                new FakeOperations(), store, characters, WorldId.Primary, SandboxSscMode.Inherit, logger,
                timeProvider: new NoPollTimeProvider(), primaryCharacterStore: store);
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => characters.ApplyStarted);
            Assert.That(coordinator.GetCharacterReadiness(player), Is.EqualTo(SscCharacterReadiness.Restoring));
            accounts.Logout();
            Assert.That(coordinator.GetCharacterReadiness(player), Is.EqualTo(SscCharacterReadiness.Unauthenticated));
            characters.ApplyRelease = null;
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => logger.PrimaryRestores == 1);
            Assert.That(coordinator.IsCharacterReady(player), Is.True);
            Assert.That(previousApply.Task.IsCompleted, Is.False);
            Assert.That(characters.AppliedPayload, Is.EqualTo("server-starter"));
            Assert.That(characters.InitialCount, Is.EqualTo(1));
            Assert.That(logger.PrimaryRestores, Is.EqualTo(1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task FailedRestoreRemainsBlockedAndNewBindingCanRetryServerState()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-ssc-restore-retry", Guid.NewGuid().ToString("N"));
        try
        {
            var player = Player(new(7), PlayerSessionId.New(), null, WorldId.Primary);
            var accounts = new FakeAccounts();
            var characters = new FakeCharacters { FailAuthenticatedApply = true };
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            using var coordinator = new SandboxSscSessionCoordinator(new FakePlayers(player), new FakeLifecycle(), accounts,
                new FakeOperations(), store, characters, WorldId.Primary, SandboxSscMode.Inherit, new RecordingLogger(),
                timeProvider: new NoPollTimeProvider(), primaryCharacterStore: store);
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => coordinator.GetCharacterReadiness(player) == SscCharacterReadiness.Failed);
            Assert.That(coordinator.IsGameplayAllowed(player, true), Is.False);
            Assert.That(store.Read("42", WorldId.Primary, SandboxSscMode.Inherit)?.Payload, Is.EqualTo("server-starter"));
            accounts.Logout();
            characters.FailAuthenticatedApply = false;
            accounts.SetBinding(new(player.Id, player.SessionId!.Value, new AccountId(42), Guid.NewGuid()));
            await EventuallyAsync(() => coordinator.IsCharacterReady(player));
            Assert.That(characters.AppliedPayload, Is.EqualTo("server-starter"));
            Assert.That(characters.InitialCount, Is.EqualTo(1));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public void ReadinessOptionsAreOptInAndReadyRequiresEnabledPersistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-primary-options", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "options.json");
            Assert.That(PrimaryCharacterPersistenceOptions.Load(path).GameplayGateEnabled, Is.False);
            File.WriteAllText(path, "{\"requireLogin\":true}");
            var login = PrimaryCharacterPersistenceOptions.Load(path);
            Assert.That(login.Enabled, Is.False);
            Assert.That(login.GameplayGateEnabled, Is.True);
            File.WriteAllText(path, "{\"requireCharacterReady\":true}");
            Assert.Throws<InvalidDataException>(() => PrimaryCharacterPersistenceOptions.Load(path));
            File.WriteAllText(path, "{\"enabled\":true,\"requireCharacterReady\":true}");
            Assert.That(PrimaryCharacterPersistenceOptions.Load(path).GameplayGateEnabled, Is.True);
            File.WriteAllText(path, "{\"authoritative\":true}");
            Assert.Throws<InvalidDataException>(() => PrimaryCharacterPersistenceOptions.Load(path));
            File.WriteAllText(path, "{\"enabled\":true,\"authoritative\":true}");
            var authoritative = PrimaryCharacterPersistenceOptions.Load(path);
            Assert.That(authoritative.GameplayGateEnabled, Is.True);
            Assert.That(authoritative.EffectiveRequireCharacterReady, Is.True);
        }
        finally { Directory.Delete(root, true); }
    }

    private static PlayerSnapshot Player(PlayerId id, PlayerSessionId sessionId, string? accountId, WorldId worldId) =>
        new(id, "alice", false, 100, 100, 20, 20, new PlayerPosition(0, 0), false, 0)
        {
            SessionId = sessionId,
            WorldId = worldId,
            Connection = new ConnectionMetadataSnapshot(
                new ConnectionId(id.Slot),
                "127.0.0.1:5000",
                "203.0.113.10:5000",
                ConnectionForwardingProvenance.TrustedForwarder,
                ClientPlatformInfo.Unknown,
                "00000000-0000-0000-0000-000000000001",
                accountId is null ? ConnectionLoginPhase.FullyConnected : ConnectionLoginPhase.WorldRequested)
            {
                TrustedTransferAccountId = accountId
            }
        };

    private static async Task EventuallyAsync(Func<bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline) Assert.Fail("Condition was not reached before timeout.");
            await Task.Delay(25);
        }
    }

    private sealed class FakePlayers(params PlayerSnapshot[] players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> Players { get; set; } = players;
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Players;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = Players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }
        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = Players.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class FakeLifecycle : IPlayerSessionLifecycle, IPlayerSessionClosing, IPlayerSessionEnteredWorld
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;
        public event Action<PlayerSessionClosed>? Closing;
        public event Action<PlayerSnapshot>? EnteredWorld;
        public void Enter(PlayerSnapshot player) => EnteredWorld?.Invoke(player);
        public void BeginClose(PlayerSessionId sessionId, PlayerId playerId) =>
            Closing?.Invoke(new PlayerSessionClosed(sessionId, playerId, "alice", DateTimeOffset.UtcNow));
        public void Close(PlayerSessionId sessionId, PlayerId playerId) =>
            Closed?.Invoke(new PlayerSessionClosed(sessionId, playerId, "alice", DateTimeOffset.UtcNow));
        public void Open(PlayerSessionId sessionId, PlayerId playerId) =>
            Opened?.Invoke(new PlayerSessionOpened(sessionId, playerId, "alice", DateTimeOffset.UtcNow));
    }

    private sealed class FakeAccounts : IPlayerAccountSessionService
    {
        private readonly object sync = new();
        private PlayerAccountBinding? binding;
        public event Action<PlayerAccountBinding>? BindingClosing;
        public event Action<PlayerAccountBinding>? BindingEstablished;
        public void SetBinding(PlayerAccountBinding? value)
        {
            lock (sync) binding = value;
            if (value is { } established) BindingEstablished?.Invoke(established);
        }
        public void Logout()
        {
            lock (sync)
            {
                if (binding is { } current) BindingClosing?.Invoke(current);
                binding = null;
            }
        }
        public bool TryGetBinding(PlayerSnapshot player, out PlayerAccountBinding value)
        {
            lock (sync)
            {
                value = binding.GetValueOrDefault();
                return binding.HasValue && player.Id == value.PlayerId && player.SessionId == value.SessionId;
            }
        }
        public bool TryRunWithBinding<T>(PlayerAccountBinding expected, Func<T> operation, out T result)
        {
            lock (sync)
            {
                result = default!;
                if (expected != binding) return false;
                result = operation();
                return true;
            }
        }
        public string? RestoredAccountId { get; private set; }
        public int TransferEstablishCount { get; private set; }
        public bool TryGetAccount(PlayerId playerId, out AccountSnapshot? account) { account = null; return false; }
        public ValueTask<bool> SetCultureAsync(PlayerId playerId, string? culture, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
        public ValueTask<bool> EstablishTrustedTransferAsync(PlayerId playerId, string accountId, CancellationToken cancellationToken = default)
        {
            RestoredAccountId = accountId;
            return ValueTask.FromResult(true);
        }
        public ValueTask<PlayerAccountBinding?> EstablishTrustedTransferBindingAsync(PlayerSnapshot player, string accountId, CancellationToken cancellationToken = default)
        {
            lock (sync)
            {
                TransferEstablishCount++;
                RestoredAccountId = accountId;
                if (player.SessionId is not { } sessionId || !int.TryParse(accountId, out int numericId))
                    return ValueTask.FromResult<PlayerAccountBinding?>(null);
                binding = new PlayerAccountBinding(player.Id, sessionId, new AccountId(numericId), Guid.NewGuid());
                return ValueTask.FromResult(binding);
            }
        }
    }

    private sealed class FakeOperations : IPlayerOperations
    {
        public int KickCount { get; private set; }
        public ValueTask<PlayerOperationResult> KickAsync(PlayerId id, string reason, CancellationToken cancellationToken = default)
        {
            KickCount++;
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerId id, PlayerPosition destination, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlayerOperationResult.Applied());
    }

    private sealed class FakeCharacters : ISandboxSscCharacterAdapter
    {
        public bool FailAuthenticatedApply { get; set; }
        public TaskCompletionSource? ApplyRelease { get; set; }
        private int applyStarted;
        public bool ApplyStarted => Volatile.Read(ref applyStarted) != 0;
        public bool DeferCapture { get; set; }
        private int deferredCancelled;
        public bool DeferredCancelled => Volatile.Read(ref deferredCancelled) != 0;
        public int InitialCount { get; private set; }
        public string CapturePayload { get; set; } = "captured";
        public string? EarlyJoinPayload { get; set; }
        public string? AppliedPayload { get; private set; }
        public ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(PlayerId playerId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SandboxSscCharacterCaptureResult(true, CapturePayload));
        public ValueTask<SandboxSscCharacterCaptureResult> CaptureJoinAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new SandboxSscCharacterCaptureResult(true, EarlyJoinPayload ?? CapturePayload));
        public ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(PlayerId playerId, string payload, CancellationToken cancellationToken = default)
        {
            AppliedPayload = payload;
            return ValueTask.FromResult(new SandboxSscCharacterApplyResult(true));
        }
        public ValueTask<SandboxSscCharacterCaptureResult> CreateInitialAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
            PlayerAccountBinding binding, CancellationToken cancellationToken = default) => ValueTask.FromResult(
            accounts.TryRunWithBinding(binding, () => { InitialCount++; return new SandboxSscCharacterCaptureResult(true, "server-starter"); }, out var result)
                ? result : new SandboxSscCharacterCaptureResult(false));
        public ValueTask<SandboxSscCharacterCaptureResult> CaptureAuthenticatedAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
            PlayerAccountBinding binding, CancellationToken cancellationToken = default) => DeferCapture ? CaptureDeferredAsync(cancellationToken) : ValueTask.FromResult(
                accounts.TryRunWithBinding(binding, () => new SandboxSscCharacterCaptureResult(true, CapturePayload), out var result)
                    ? result : new SandboxSscCharacterCaptureResult(false));
        private async ValueTask<SandboxSscCharacterCaptureResult> CaptureDeferredAsync(CancellationToken cancellationToken)
        {
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) { Interlocked.Exchange(ref deferredCancelled, 1); throw; }
            return new SandboxSscCharacterCaptureResult(true, CapturePayload);
        }
        public async ValueTask<SandboxSscCharacterApplyResult> ApplyAuthenticatedAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
            PlayerAccountBinding binding, string payload, CancellationToken cancellationToken = default)
        {
            if (ApplyRelease is { } release)
            {
                Interlocked.Exchange(ref applyStarted, 1);
                await release.Task.WaitAsync(cancellationToken);
            }
            return accounts.TryRunWithBinding(binding, () => { if (FailAuthenticatedApply) return new SandboxSscCharacterApplyResult(false); AppliedPayload = payload; return new SandboxSscCharacterApplyResult(true); }, out var result)
                ? result : new SandboxSscCharacterApplyResult(false);
        }
    }

    private sealed class CommandPlayer(PlayerSnapshot player, string[] permissions) : IPlayerCommandPrincipal
    {
        public string Name => player.Name;
        public bool IsServer => false;
        public string CultureSubjectId => player.Name;
        public PlayerId PlayerId => player.Id;
        public PlayerSessionId? SessionId => player.SessionId;
        public string PlayerName => player.Name;
        public string? AccountName => player.Name;
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        private int restores;
        private int transferRestores;
        public int PrimaryRestores => Volatile.Read(ref restores);
        public int TransferRestores => Volatile.Read(ref transferRestores);
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null)
        {
            if (text == "Authenticated primary character restored.") Interlocked.Increment(ref restores);
            if (text.StartsWith("Accepted transferred player slot", StringComparison.Ordinal)) Interlocked.Increment(ref transferRestores);
        }
    }

    private sealed class OffsetTimeProvider : TimeProvider
    {
        private long ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref ticks));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class NoPollTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoPollTimer();
        private sealed class NoPollTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
