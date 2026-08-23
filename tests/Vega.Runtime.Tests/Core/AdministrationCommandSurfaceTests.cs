using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Configuration;
using Vega.Persistence.Sqlite;
using Vega.Regions;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class AdministrationCommandSurfaceTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp() => directory = Path.Combine(
        Path.GetTempPath(), "Vega.AdministrationSurface.Tests", Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [Test]
    public async Task DispatcherValidatesConfigAndPersistsGroupPermissionAndWorldRegion()
    {
        Directory.CreateDirectory(directory);
        string configPath = Path.Combine(directory, "server.json");
        await File.WriteAllTextAsync(configPath,
            """{"port":7788,"maximumPlayers":12,"defaultCulture":"en-US","uiMode":"plain"}""");
        SqliteVegaDatabase database = await SqliteVegaDatabase.OpenAsync(
            new SqliteVegaDatabaseOptions(Path.Combine(directory, "data", "vega.db")));
        var dispatcher = new CommandDispatcher([
            new ConfigurationCommand(new VegaServerConfigurationState(configPath, [])),
            new GroupCommand(database.Authorization),
            new RegionCommand(database.Regions)
        ]);
        var world = new WorldId("world-operator-smoke");

        CommandExecutionResult validated = await Dispatch(dispatcher, "config", ["validate"], world);
        CommandExecutionResult createdGroup = await Dispatch(dispatcher, "group", ["create", "builders", "default"], world);
        CommandExecutionResult granted = await Dispatch(dispatcher, "group", ["grant", "builders", "vega.world.build"], world);
        CommandExecutionResult createdRegion = await Dispatch(dispatcher, "region",
            ["create", "spawn", "0", "0", "40", "30", "10", "OfflineOwner"], world);
        CommandExecutionResult assignedGroup = await Dispatch(dispatcher, "region",
            ["group", "spawn", "add", "builders"], world);

        Assert.Multiple(() =>
        {
            Assert.That(new[] { validated, createdGroup, granted, createdRegion, assignedGroup }
                .All(static result => result.Status == CommandExecutionStatus.Success), Is.True);
            Assert.That(database.Authorization.HasPermission("builders", "vega.world.build"), Is.True);
            Assert.That(database.Regions.SnapshotRegions(world).Single().Groups, Does.Contain("builders"));
            Assert.That(database.Regions.SnapshotRegions(WorldId.Primary), Is.Empty);
        });
    }

    private static ValueTask<CommandExecutionResult> Dispatch(
        CommandDispatcher dispatcher,
        string name,
        IReadOnlyList<string> arguments,
        WorldId world) => dispatcher.DispatchAsync(
        new CommandInvocation(name, arguments, ServerPrincipal.Instance, NullOutput.Instance) { WorldId = world });

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();
        public string Name => "Server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
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
