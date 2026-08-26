using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Terraria.Commands;
using Vega.Terraria.Items;

namespace Vega.Runtime.Tests.Terraria;

[TestFixture]
public sealed class TerrariaItemCommandTests
{
    private static readonly PlayerSnapshot Alice = Player(3, "Alice");
    private static readonly PlayerSnapshot Bob = Player(8, "Bob");

    [Test]
    public void TerrariaCatalogResolvesNumericIdsAndItemTags()
    {
        var catalog = new TerrariaItemCatalog();

        ItemResolutionResult numeric = catalog.Resolve("1");
        ItemResolutionResult tag = catalog.Resolve("[i/s5/p2:1]");

        Assert.Multiple(() =>
        {
            Assert.That(numeric.IsFound, Is.True);
            Assert.That(numeric.Item!.Type, Is.EqualTo(1));
            Assert.That(numeric.Item.MaxStack, Is.GreaterThan(0));
            Assert.That(tag.Item!.Type, Is.EqualTo(1));
        });
    }

    [Test]
    public void TerrariaCatalogAppliesAccessoryQuickPrefixCompatibility()
    {
        var catalog = new TerrariaItemCatalog();
        var accessory = new ItemDefinition(50, "Accessory", 1, IsAccessory: true);
        var normal = new ItemDefinition(1, "Item", 1);

        ItemPrefixResolutionResult accessoryQuick = catalog.ResolvePrefix(
            global::Terraria.ID.PrefixID.Quick.ToString(),
            accessory);
        ItemPrefixResolutionResult explicitQuickTwo = catalog.ResolvePrefix(
            global::Terraria.ID.PrefixID.Quick2.ToString(),
            normal);

        Assert.Multiple(() =>
        {
            Assert.That(accessoryQuick.Prefix!.Id, Is.EqualTo(global::Terraria.ID.PrefixID.Quick2));
            Assert.That(explicitQuickTwo.Prefix!.Id, Is.EqualTo(global::Terraria.ID.PrefixID.Quick2));
        });
    }

    [Test]
    public void DescriptorsMatchGiveAndItemSurface()
    {
        var inventory = new RecordingInventory();
        var catalog = new FakeCatalog();
        var service = new ItemGrantCommandService(inventory, catalog);

        CommandDescriptor give = new GiveCommand(new FakePlayers(Bob), new RecordingMessenger(), service).Descriptor;
        CommandDescriptor item = new ItemCommand(new FakePlayers(Alice), service).Descriptor;

        Assert.Multiple(() =>
        {
            Assert.That(give.Aliases, Is.EqualTo(new[] { "g" }));
            Assert.That(give.Permissions, Is.EqualTo(new[] { "vega.inventory.give" }));
            Assert.That(give.AllowServer, Is.True);
            Assert.That(item.Aliases, Is.EqualTo(new[] { "i" }));
            Assert.That(item.Permissions, Is.EqualTo(new[] { "vega.inventory.item" }));
            Assert.That(item.AllowServer, Is.False);
        });
    }

    [Test]
    public async Task DispatcherEnforcesPermissionsConsolePolicyAndAliases()
    {
        var inventory = new RecordingInventory();
        var service = new ItemGrantCommandService(inventory, new FakeCatalog());
        var players = new FakePlayers(Alice, Bob);
        var dispatcher = new CommandDispatcher([
            new GiveCommand(players, new RecordingMessenger(), service),
            new ItemCommand(players, service)
        ]);

        CommandExecutionResult giveFromServer = await dispatcher.DispatchAsync(
            Invocation("g", ["stone", "Bob"], new ServerPrincipal("vega.inventory.give")));
        CommandExecutionResult itemFromServer = await dispatcher.DispatchAsync(
            Invocation("i", ["stone"], new ServerPrincipal("vega.inventory.item")));
        CommandExecutionResult giveWithoutPermission = await dispatcher.DispatchAsync(
            Invocation("give", ["stone", "Bob"], new PlayerPrincipal(Alice)));
        CommandExecutionResult itemWithoutPermission = await dispatcher.DispatchAsync(
            Invocation("item", ["stone"], new PlayerPrincipal(Alice)));

        Assert.Multiple(() =>
        {
            Assert.That(giveFromServer.Status, Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(itemFromServer.ErrorCode, Is.EqualTo("command.server_not_allowed"));
            Assert.That(giveWithoutPermission.ErrorCode, Is.EqualTo("command.permission_denied"));
            Assert.That(itemWithoutPermission.ErrorCode, Is.EqualTo("command.permission_denied"));
        });
    }

    [Test]
    public async Task GiveRejectsMissingAndAmbiguousTargets()
    {
        var service = new ItemGrantCommandService(new RecordingInventory(), new FakeCatalog());
        var command = new GiveCommand(
            new FakePlayers(Player(1, "Alex"), Player(2, "Alexa")),
            new RecordingMessenger(),
            service);

        CommandExecutionResult missing = await command.ExecuteAsync(
            Invocation("give", ["stone", "Nobody"]),
            CancellationToken.None);
        CommandExecutionResult ambiguous = await command.ExecuteAsync(
            Invocation("give", ["stone", "Ale"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(missing.ErrorCode, Is.EqualTo("player.not_found"));
            Assert.That(ambiguous.ErrorCode, Is.EqualTo("player.ambiguous"));
        });
    }

    [Test]
    public async Task CommandsRejectMissingAndAmbiguousItems()
    {
        var service = new ItemGrantCommandService(new RecordingInventory(), new FakeCatalog());
        var command = new GiveCommand(new FakePlayers(Bob), new RecordingMessenger(), service);

        CommandExecutionResult missing = await command.ExecuteAsync(
            Invocation("give", ["missing", "Bob"]),
            CancellationToken.None);
        CommandExecutionResult ambiguous = await command.ExecuteAsync(
            Invocation("give", ["ambiguous", "Bob"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(missing.ErrorCode, Is.EqualTo("item.not_found"));
            Assert.That(ambiguous.ErrorCode, Is.EqualTo("item.ambiguous"));
        });
    }

    [Test]
    public async Task DefaultAmountUsesMaxStackOrOnlyNeedOneAndExplicitAmountIsClamped()
    {
        var inventory = new RecordingInventory();
        var service = new ItemGrantCommandService(inventory, new FakeCatalog());
        var give = new GiveCommand(new FakePlayers(Bob), new RecordingMessenger(), service);
        var item = new ItemCommand(new FakePlayers(Alice), service);

        await give.ExecuteAsync(Invocation("give", ["stone", "Bob"]), CancellationToken.None);
        await item.ExecuteAsync(
            Invocation("item", ["magic", "mirror"], new PlayerPrincipal(Alice, "vega.inventory.item")),
            CancellationToken.None);
        await give.ExecuteAsync(Invocation("give", ["stone", "Bob", "2000"]), CancellationToken.None);

        Assert.That(
            inventory.Requests.Select(static request => request.Stack),
            Is.EqualTo(new[] { 999, 1, 999 }));
    }

    [Test]
    public async Task PrefixIdAndNameUseSharedPrefixResolver()
    {
        var inventory = new RecordingInventory();
        var catalog = new FakeCatalog();
        var service = new ItemGrantCommandService(inventory, catalog);
        var give = new GiveCommand(new FakePlayers(Bob), new RecordingMessenger(), service);
        var item = new ItemCommand(new FakePlayers(Alice), service);

        await give.ExecuteAsync(Invocation("give", ["stone", "Bob", "5", "2"]), CancellationToken.None);
        await item.ExecuteAsync(
            Invocation("item", ["stone", "5", "Legendary"], new PlayerPrincipal(Alice, "vega.inventory.item")),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(inventory.Requests.Select(static request => request.Prefix), Is.EqualTo(new byte[] { 2, 3 }));
            Assert.That(catalog.PrefixQueries, Is.EqualTo(new[] { "2", "Legendary" }));
        });
    }

    [Test]
    public async Task InventoryFullIsReportedWithoutSuccessOrNotification()
    {
        var inventory = new RecordingInventory
        {
            Results = [new InventoryMutationResult(InventoryMutationStatus.InventoryFull)]
        };
        var messenger = new RecordingMessenger();
        var command = new GiveCommand(
            new FakePlayers(Bob),
            messenger,
            new ItemGrantCommandService(inventory, new FakeCatalog()));

        CommandExecutionResult result = await command.ExecuteAsync(
            Invocation("give", ["stone", "Bob", "5"]),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ErrorCode, Is.EqualTo("inventory.full"));
            Assert.That(messenger.Messages, Is.Empty);
        });
    }

    [Test]
    public async Task RevisionConflictRecapturesOnceThenRetriesOnce()
    {
        var inventory = new RecordingInventory
        {
            CaptureRevisions = [10, 20],
            Results =
            [
                new InventoryMutationResult(InventoryMutationStatus.RevisionConflict),
                new InventoryMutationResult(InventoryMutationStatus.Applied)
            ]
        };
        var service = new ItemGrantCommandService(inventory, new FakeCatalog());
        ItemGrantPlan plan = service.Resolve("stone", 5, null).Plan!;

        InventoryMutationResult result = await service.GrantAsync(Bob.Id, plan, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InventoryMutationStatus.Applied));
            Assert.That(inventory.CaptureCount, Is.EqualTo(2));
            Assert.That(inventory.ExpectedRevisions, Is.EqualTo(new long[] { 10, 20 }));
        });
    }

    [Test]
    public async Task PersistentRevisionConflictStopsAfterSecondGrant()
    {
        var inventory = new RecordingInventory
        {
            Results =
            [
                new InventoryMutationResult(InventoryMutationStatus.RevisionConflict),
                new InventoryMutationResult(InventoryMutationStatus.RevisionConflict)
            ]
        };
        var service = new ItemGrantCommandService(inventory, new FakeCatalog());

        InventoryMutationResult result = await service.GrantAsync(
            Bob.Id,
            service.Resolve("stone", 5, null).Plan!,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(InventoryMutationStatus.RevisionConflict));
            Assert.That(inventory.GrantCount, Is.EqualTo(2));
            Assert.That(inventory.CaptureCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ItemGrantsCallerWhileGiveGrantsSelectedTargetAndNotifiesThem()
    {
        var inventory = new RecordingInventory();
        var messenger = new RecordingMessenger();
        var service = new ItemGrantCommandService(inventory, new FakeCatalog());
        var players = new FakePlayers(Alice, Bob);
        var item = new ItemCommand(players, service);
        var give = new GiveCommand(players, messenger, service);

        await item.ExecuteAsync(
            Invocation("item", ["stone", "2"], new PlayerPrincipal(Alice, "vega.inventory.item")),
            CancellationToken.None);
        await give.ExecuteAsync(
            Invocation("give", ["stone", "Bob", "3"], new PlayerPrincipal(Alice, "vega.inventory.give")),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(inventory.PlayerIds, Is.EqualTo(new[] { Alice.Id, Bob.Id }));
            Assert.That(messenger.Messages, Has.Count.EqualTo(1));
            Assert.That(messenger.Messages[0].PlayerId, Is.EqualTo(Bob.Id));
            Assert.That(messenger.Messages[0].Text, Does.Contain("Alice"));
        });
    }

    private static CommandInvocation Invocation(
        string name,
        IReadOnlyList<string> arguments,
        ICommandPrincipal? principal = null) =>
        new(name, arguments, principal ?? new ServerPrincipal("vega.inventory.give"), NullOutput.Instance)
        {
            WorldId = WorldId.Primary
        };

    private static PlayerSnapshot Player(int slot, string name) => new(
        new PlayerId(slot),
        name,
        false,
        100,
        100,
        20,
        20,
        new PlayerPosition(0, 0),
        false,
        0);

    private sealed class FakeCatalog : IItemCatalog
    {
        public List<string> PrefixQueries { get; } = [];

        public ItemResolutionResult Resolve(string query) => query.ToLowerInvariant() switch
        {
            "stone" => Found(new ItemDefinition(1, "Stone Block", 999)),
            "magic mirror" => Found(new ItemDefinition(50, "Magic Mirror", 1, OnlyNeedOneInInventory: true)),
            "ambiguous" => new ItemResolutionResult(
                ItemResolutionStatus.Ambiguous,
                Matches: [new ItemDefinition(2, "Stone Wall", 999), new ItemDefinition(3, "Stone Slab", 999)]),
            _ => new ItemResolutionResult(ItemResolutionStatus.NotFound)
        };

        public ItemPrefixResolutionResult ResolvePrefix(string query, ItemDefinition item)
        {
            PrefixQueries.Add(query);
            return query switch
            {
                "2" => FoundPrefix(2, "Strong"),
                "Legendary" => FoundPrefix(3, "Legendary"),
                _ => new ItemPrefixResolutionResult(ItemResolutionStatus.NotFound)
            };
        }

        private static ItemResolutionResult Found(ItemDefinition item) =>
            new(ItemResolutionStatus.Found, item);

        private static ItemPrefixResolutionResult FoundPrefix(byte id, string name) =>
            new(ItemResolutionStatus.Found, new ItemPrefixDefinition(id, name));
    }

    private sealed class RecordingInventory : IPlayerInventoryGrantService
    {
        private int resultIndex;
        private int captureIndex;

        public long[] CaptureRevisions { get; init; } = [10];
        public InventoryMutationResult[] Results { get; init; } = [];
        public List<InventoryGrantRequest> Requests { get; } = [];
        public List<PlayerId> PlayerIds { get; } = [];
        public List<long> ExpectedRevisions { get; } = [];
        public int CaptureCount { get; private set; }
        public int GrantCount { get; private set; }

        public ValueTask<PlayerInventorySnapshot?> CaptureAsync(
            PlayerId playerId,
            CancellationToken cancellationToken = default)
        {
            CaptureCount++;
            long revision = CaptureRevisions[Math.Min(captureIndex++, CaptureRevisions.Length - 1)];
            return ValueTask.FromResult<PlayerInventorySnapshot?>(
                new PlayerInventorySnapshot(playerId, "Player", revision, [], DateTimeOffset.UtcNow));
        }

        public ValueTask<InventoryMutationResult> GrantAsync(
            PlayerId playerId,
            long expectedRevision,
            InventoryGrantRequest request,
            CancellationToken cancellationToken = default)
        {
            GrantCount++;
            PlayerIds.Add(playerId);
            ExpectedRevisions.Add(expectedRevision);
            Requests.Add(request);
            InventoryMutationResult result = resultIndex < Results.Length
                ? Results[resultIndex++]
                : new InventoryMutationResult(
                    InventoryMutationStatus.Applied,
                    new PlayerInventorySnapshot(playerId, "Player", expectedRevision + 1, [], DateTimeOffset.UtcNow));
            return ValueTask.FromResult(result);
        }

        public ValueTask<InventoryMutationResult> ReplaceSlotAsync(
            PlayerId playerId,
            long expectedRevision,
            InventorySlotReplacement replacement,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePlayers(params PlayerSnapshot[] players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;

        public bool TryGet(PlayerId id, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate => candidate.Id == id);
            return player is not null;
        }

        public bool TryFindExact(string name, out PlayerSnapshot? player)
        {
            player = players.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            return player is not null;
        }
    }

    private sealed class RecordingMessenger : IPlayerMessenger
    {
        public List<(PlayerId PlayerId, string Text)> Messages { get; } = [];

        public ValueTask<PlayerOperationResult> SendMessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default)
        {
            Messages.Add((id, message));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
    }

    private sealed class PlayerPrincipal(PlayerSnapshot player, params string[] permissions) : IPlayerCommandPrincipal
    {
        public PlayerId PlayerId => player.Id;
        public string PlayerName => player.Name;
        public string? AccountName => null;
        public string Name => player.Name;
        public bool IsServer => false;
        public string CultureSubjectId => $"player:{player.Id.Slot}";
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class ServerPrincipal(params string[] permissions) : ICommandPrincipal
    {
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => permissions.Contains(permission, StringComparer.Ordinal);
    }

    private sealed class NullOutput : ICommandOutput
    {
        public static NullOutput Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
