using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.System;
using Vega.Modules.Administration;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Commands;

[TestFixture]
public sealed class ServerInformationCommandsTests
{
    [Test]
    public async Task DeathStatisticsSelectTheNativeCounterAndCurrentWorldOnly()
    {
        var world = new WorldId("arena");
        PlayerSnapshot alice = Player(1, "Alice", world, 2, 5);
        var players = new Directory([alice, Player(2, "Bob", world, 7, 1), Player(3, "Other", new("primary"), 99, 99)]);
        var dispatcher = new CommandDispatcher([new PlayerDeathStatisticsCommand(players, false, false), new PlayerDeathStatisticsCommand(players, true, false),
            new PlayerDeathStatisticsCommand(players, false, true), new PlayerDeathStatisticsCommand(players, true, true)]);
        var invocation = new CommandInvocation("death", [], new Principal(alice), new Output()) { WorldId = world };
        Assert.That((await dispatcher.DispatchAsync(invocation)).Messages!.Single().Arguments, Is.EqualTo(new object[]{"Alice", "PvE", 2}));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Name = "pvpdeath" })).Messages!.Single().Arguments, Is.EqualTo(new object[]{"Alice", "PvP", 5}));
        var all = await dispatcher.DispatchAsync(invocation with { Name = "alldeath" });
        Assert.That(all.Messages!.Skip(1).Select(m => m.Arguments[0]), Is.EqualTo(new[]{"Bob", "Alice"}));
        Assert.That((await dispatcher.DispatchAsync(invocation with { Name = "allpvpdeath", Arguments = ["2147483647"] })).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
    }

    [Test]
    public async Task ServerTextUsesWorldOverrideAndBoundsOperatorText()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-server-text", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            string primaryPath = Path.Combine(root, "world.txt"), deploymentPath = Path.Combine(root, "default.txt");
            await File.WriteAllTextAsync(deploymentPath, "Default rules\nBe kind");
            var handler = new ServerTextCommand("rules", primaryPath, deploymentPath);
            var command = new CommandInvocation("rules", [], new Principal(Player(1, "Alice", new("arena"), 0, 0)), new Output());
            Assert.That((await handler.ExecuteAsync(command, default)).Messages!.Select(m => m.Arguments[0]), Is.EqualTo(new[]{"Default rules", "Be kind"}));
            await File.WriteAllTextAsync(primaryPath, "Arena rules");
            Assert.That((await handler.ExecuteAsync(command, default)).Messages!.Single().Arguments[0], Is.EqualTo("Arena rules"));
            await File.WriteAllTextAsync(primaryPath, new string('x', 16385));
            Assert.That((await handler.ExecuteAsync(command, default)).Status, Is.EqualTo(CommandExecutionStatus.Failed));
        }
        finally { System.IO.Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task LogViewingIsBoundedAndReferenceExportIsWorldScoped()
    {
        var logs = new BoundedOperationsLogStore(30);
        logs.Emit(new(1, DateTimeOffset.UtcNow, OperationsLogLevel.Information, "test", "info"));
        logs.Emit(new(2, DateTimeOffset.UtcNow, OperationsLogLevel.Warning, "test", "warning\nline", "private stack detail"));
        var exporter = new Exporter();
        var dispatcher = new CommandDispatcher([new DisplayLogsCommand(logs), new ReferenceDataCommand(exporter)]);
        var world = new WorldId("arena");
        var command = new CommandInvocation("displaylogs", ["1", "warning"], new Principal(Player(1, "Alice", world, 0, 0)), new Output()) { WorldId = world };
        var result = await dispatcher.DispatchAsync(command);
        Assert.That(result.Messages!.Single().Arguments[3], Is.EqualTo("warning line"));
        Assert.That((await dispatcher.DispatchAsync(command with { Arguments = ["21"] })).Status, Is.EqualTo(CommandExecutionStatus.InvalidUsage));
        Assert.That((await dispatcher.DispatchAsync(command with { Name = "dump-reference-data", Arguments = ["tile"] })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(exporter.Last, Is.EqualTo((world, WorldReferenceKind.Tile)));
    }

    private sealed class Exporter : IWorldReferenceDataExporter
    {
        public (WorldId, WorldReferenceKind)? Last { get; private set; }
        public ValueTask<WorldReferenceExportResult> ExportAsync(WorldId worldId, WorldReferenceKind kind, CancellationToken cancellationToken = default)
        { Last = (worldId, kind); return ValueTask.FromResult(new WorldReferenceExportResult(true, "reference/tile.json", 10)); }
    }

    private static PlayerSnapshot Player(int id, string name, WorldId world, int pve, int pvp) =>
        new(new(id), name, false, 100, 100, 20, 20, new(0, 0), false, 0) { WorldId = world, SessionId = PlayerSessionId.New(), DeathsPve = pve, DeathsPvp = pvp };

    private sealed class Directory(PlayerSnapshot[] players) : IPlayerDirectory
    {
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = players.FirstOrDefault(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = players.FirstOrDefault(p => p.Name == name)) is not null;
    }

    private sealed class Principal(PlayerSnapshot player) : IPlayerCommandPrincipal
    {
        public string Name => player.Name; public bool IsServer => false; public string CultureSubjectId => Name;
        public PlayerId PlayerId => player.Id; public PlayerSessionId? SessionId => player.SessionId;
        public string PlayerName => player.Name; public string? AccountName => null;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
