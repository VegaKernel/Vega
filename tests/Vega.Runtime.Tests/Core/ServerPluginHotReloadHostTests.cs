using NUnit.Framework;
using System.Text;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ServerPluginHotReloadHostTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "vega-plugin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task PluginOwnedFakeProvidersAndVisibilityAreDetachedOnRealReload()
    {
        var scenes = new SceneRuntime();
        var visibility = new SceneVisibilityRuntime();
        VegaPluginRuntime.Configure(new VegaPluginRuntimeServices(WorldId.Primary, null, null, null, null, scenes, null, visibility));
        try
        {
            string pluginPath = FindTestPlugin();
            File.Copy(pluginPath, Path.Combine(directory, Path.GetFileName(pluginPath)));
            using var host = new ServerPluginHotReloadHost(directory, new ServerPluginHotReloadOptions(true, 100, 5000),
                new CommandDispatcher([]), new RecordingLogger());
            host.Start();
            var originalScene = scenes.SnapshotScenes().Single();
            var original = originalScene.Id;
            var originalOverlay = originalScene.Overlays.Single();
            var originalChest = originalOverlay.VirtualChests.Single().Id;
            var originalSign = originalOverlay.VirtualSigns.Single().Id;
            var opened = new PlayerSessionOpened(PlayerSessionId.New(), new(1), "viewer", DateTimeOffset.UtcNow);
            scenes.OpenSession(WorldId.Primary, opened);
            var player = new PlayerSnapshot(opened.PlayerId, opened.PlayerName, false, 100, 100, 20, 20, new(160, 160), false, 0)
                { WorldId = WorldId.Primary, SessionId = opened.SessionId };
            Assert.That(scenes.TryRequestVirtualChestOpen(player, 10, 10), Is.True);
            Assert.That(scenes.TryRequestVirtualSignRead(player, 12, 10), Is.True);
            Assert.That(visibility.SnapshotLayers(), Has.Count.EqualTo(1));
            var reload = await host.ReloadAsync("Vega.Test.HotPlugin");
            Assert.That(reload.Succeeded, Is.True, reload.Message);
            Assert.That(reload.RestartRequired, Is.False);
            Assert.That(scenes.SnapshotScenes().Single().Id, Is.Not.EqualTo(original));
            Assert.That(scenes.TryTakeVirtualChestOpen(original, originalOverlay.Id, originalChest, out _), Is.False);
            Assert.That(scenes.TryTakeVirtualSignRead(original, originalOverlay.Id, originalSign, out _), Is.False);
            Assert.That(scenes.TryResolveVirtualSign(WorldId.Primary, SceneSessionObserver.From(opened), 12, 10, out var replacementSign), Is.True);
            Assert.That(replacementSign!.Value.Definition.Id, Is.Not.EqualTo(originalSign));
            Assert.That(scenes.TryResolveVirtualChest(WorldId.Primary, SceneSessionObserver.From(opened), 10, 10, out var replacement), Is.True);
            Assert.That(replacement!.Value.Definition.Id, Is.Not.EqualTo(originalChest));
            Assert.That(visibility.SnapshotLayers(), Has.Count.EqualTo(1));
            var unload = await host.UnloadAsync("Vega.Test.HotPlugin");
            Assert.That(unload.Succeeded, Is.True, unload.Message);
            Assert.That(unload.RestartRequired, Is.False);
            Assert.That(scenes.SnapshotScenes(), Is.Empty);
            Assert.That(visibility.SnapshotLayers(), Is.Empty);
        }
        finally { VegaPluginRuntime.Configure(VegaPluginRuntimeServices.Empty); }
    }

    [Test]
    public async Task LoadsCommandAndRemovesItBeforeCollectibleContextIsReleased()
    {
        string pluginPath = FindTestPlugin();
        File.Copy(pluginPath, Path.Combine(directory, Path.GetFileName(pluginPath)));
        var dispatcher = new CommandDispatcher([]);
        var logger = new RecordingLogger();
        using var host = new ServerPluginHotReloadHost(
            directory,
            new ServerPluginHotReloadOptions(true, 100, 5000),
            dispatcher,
            logger);

        host.Start();

        Assert.Multiple(() =>
        {
            Assert.That(host.HotReloadEnabled, Is.True);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.True);
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Loaded));
        });

        var reloaded = await host.ReloadAsync("Vega.Test.HotPlugin");
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Succeeded, Is.True, reloaded.Message);
            Assert.That(reloaded.RestartRequired, Is.False);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.True);
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Loaded));
        });

        var result = await host.UnloadAsync("Vega.Test.HotPlugin");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(result.RestartRequired, Is.False);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Disabled));
        });
    }

    [Test]
    public async Task RepeatedFaultsQuarantineOnlyTheOffenderAndRetireItsCollectibleContext()
    {
        string pluginPath = FindTestPlugin();
        File.Copy(pluginPath, Path.Combine(directory, Path.GetFileName(pluginPath)));
        var dispatcher = new CommandDispatcher([new HealthyCommand()]);
        using var host = new ServerPluginHotReloadHost(directory,
            new ServerPluginHotReloadOptions(true, 100, 5000) { FaultThreshold = 2 }, dispatcher, new RecordingLogger());
        host.Start();
        for (int index = 0; index < 2; index++)
        {
            var result = await dispatcher.DispatchAsync(new CommandInvocation("hotfault", [], new Principal(), new Output()));
            Assert.That(result.ErrorCode, Is.EqualTo("plugin.callback_failed"));
        }
        Assert.That((await dispatcher.DispatchAsync(new CommandInvocation("healthy", [], new Principal(), new Output()))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        await WaitUntilAsync(() => host.SnapshotModules().Single().State == ModuleLoadState.Disabled);
        var snapshot = host.SnapshotModules().Single();
        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch("hotfault"), Is.False);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
            Assert.That(dispatcher.CanDispatch("healthy"), Is.True);
            Assert.That(snapshot.FaultCount, Is.EqualTo(2));
            Assert.That(snapshot.LastFaultOperation, Does.Contain("hotfault"));
            Assert.That(snapshot.LastFaultSummary, Does.Contain("Test-only"));
            Assert.That(host.Read().IsRestartRequired, Is.False);
        });
    }

    private sealed class HealthyCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("healthy", [], []);
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CommandExecutionResult.Success);
    }

    private sealed class Principal : ICommandPrincipal
    {
        public string Name => "test";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    [Test]
    public async Task DisabledModeRejectsAdministrativeReloadWhileMonitoringDirectory()
    {
        var dispatcher = new CommandDispatcher([]);
        using var host = new ServerPluginHotReloadHost(
            directory,
            ServerPluginHotReloadOptions.Default,
            dispatcher,
            new RecordingLogger());
        host.Start();

        var result = await host.ReloadAsync("missing.dll");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Message, Does.Contain("disabled"));
            Assert.That(host.Read().IsHotReloadEnabled, Is.False);
            Assert.That(host.Read().IsMonitored, Is.True);
        });

        string deployedPath = Path.Combine(directory, "Disabled.Plugin.dll");
        File.Copy(FindTestPlugin(), deployedPath);
        await WaitUntilAsync(() => host.Read().IsRestartRequired);

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
            Assert.That(host.Read().ChangedFiles, Does.Contain("Disabled.Plugin.dll"));
        });
    }

    [Test]
    public async Task WatcherTracksPluginLifecycle()
    {
        var dispatcher = new CommandDispatcher([]);
        using var host = new ServerPluginHotReloadHost(
            directory,
            new ServerPluginHotReloadOptions(true, 100, 5000),
            dispatcher,
            new RecordingLogger());
        host.Start();
        string deployedPath = Path.Combine(directory, "Watched.Plugin.dll");

        File.Copy(FindTestPlugin(), deployedPath);
        await WaitUntilAsync(() => dispatcher.CanDispatch("hotprobe"));

        File.Delete(deployedPath);
        await WaitUntilAsync(() =>
            host.SnapshotModules().SingleOrDefault()?.State == ModuleLoadState.Disabled);

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
            Assert.That(host.Read().ChangedFiles, Does.Contain("Watched.Plugin.dll"));
            Assert.That(host.Read().IsRestartRequired, Is.False);
        });
    }

    [Test]
    public void RejectsPluginTargetingANewerRuntimeWithActionableDiagnostic()
    {
        byte[] image = File.ReadAllBytes(FindTestPlugin());
        byte[] currentTarget = Encoding.UTF8.GetBytes(".NETCoreApp,Version=v11.0");
        byte[] newerTarget = Encoding.UTF8.GetBytes(".NETCoreApp,Version=v12.0");
        int targetOffset = image.AsSpan().IndexOf(currentTarget);
        Assert.That(targetOffset, Is.GreaterThanOrEqualTo(0), "Test plugin target framework metadata was not found.");
        newerTarget.CopyTo(image, targetOffset);
        File.WriteAllBytes(Path.Combine(directory, "Future.Plugin.dll"), image);

        var dispatcher = new CommandDispatcher([]);
        using var host = new ServerPluginHotReloadHost(
            directory,
            new ServerPluginHotReloadOptions(true, 100, 5000),
            dispatcher,
            new RecordingLogger());

        host.Start();

        Assert.Multiple(() =>
        {
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
            Assert.That(host.Read().IsRestartRequired, Is.True);
            Assert.That(host.Read().Failure, Does.Contain(".NETCoreApp,Version=v12.0"));
            Assert.That(host.Read().Failure, Does.Contain("net11.0"));
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static string FindTestPlugin()
    {
        DirectoryInfo? current = new(TestContext.CurrentContext.TestDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "src", "Vega.sln")))
        {
            current = current.Parent;
        }

        Assert.That(current, Is.Not.Null, "Repository root was not found from the test output directory.");
        string targetFramework = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Name;
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        string path = Path.Combine(
            current!.FullName,
            "tests",
            "Vega.HotReload.TestPlugin",
            "bin",
            configuration,
            targetFramework,
            "Vega.HotReload.TestPlugin.dll");
        Assert.That(File.Exists(path), Is.True, $"Test plugin was not built: {path}");
        return path;
    }

    private sealed class RecordingLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }
}
