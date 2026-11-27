using NUnit.Framework;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.Core.Scenes;
using Vega.Contracts.Worlds;
using Vega.Contracts.Threading;
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
                new ServerPluginHostServices(new CommandDispatcher([]), new RecordingLogger()));
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
        var routes = new PluginRestRegistry();
        using var host = new ServerPluginHotReloadHost(
            directory,
            new ServerPluginHotReloadOptions(true, 100, 5000),
            new ServerPluginHostServices(dispatcher, logger) { Extensions = new(Rest: routes) });

        host.Start();

        Assert.Multiple(() =>
        {
            Assert.That(host.HotReloadEnabled, Is.True);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.True);
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Loaded));
        });
        Assert.That((await dispatcher.DispatchAsync(new CommandInvocation("hotservice", [], new Principal(), new Output()))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await routes.DispatchAsync("Vega.Test.HotPlugin", "probe", PluginRestMethod.Query, [])).Status, Is.EqualTo(PluginRestStatus.Success));

        var reloaded = await host.ReloadAsync("Vega.Test.HotPlugin");
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Succeeded, Is.True, reloaded.Message);
            Assert.That(reloaded.RestartRequired, Is.False);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.True);
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Loaded));
        });
        Assert.That((await dispatcher.DispatchAsync(new CommandInvocation("hotservice", [], new Principal(), new Output()))).Status,
            Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await routes.DispatchAsync("Vega.Test.HotPlugin", "probe", PluginRestMethod.Query, [])).Status, Is.EqualTo(PluginRestStatus.Success));

        var result = await host.UnloadAsync("Vega.Test.HotPlugin");
        Assert.That((await routes.DispatchAsync("Vega.Test.HotPlugin", "probe", PluginRestMethod.Query, [])).Status, Is.EqualTo(PluginRestStatus.NotFound));

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, result.Message);
            Assert.That(result.RestartRequired, Is.False);
            Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
            Assert.That(dispatcher.CanDispatch("hotservice"), Is.False);
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
            new ServerPluginHotReloadOptions(true, 100, 5000) { FaultThreshold = 2 },
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));
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

    [Test]
    public async Task IndependentPluginFaultSoakKeepsHealthyActivationServingAcrossThirtyTwoRetirements()
    {
        DeployTestPlugins();
        foreach (string name in new[] { "Vega.Test.HotPlugin", "Vega.Test.HealthyPlugin" })
        {
            string data = Path.Combine(directory, ".data", name);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "soak-mode"), "1");
        }
        var dispatcher = new CommandDispatcher([]);
        var lifecycle = new LifecycleProbe();
        var gameThread = new ScheduledProbe();
        using var host = new ServerPluginHotReloadHost(directory,
            new ServerPluginHotReloadOptions(true, 100, 5000) { FaultThreshold = 5 },
            new ServerPluginHostServices(dispatcher, new RecordingLogger()) { Players = new(Lifecycle: lifecycle, GameThread: gameThread) });
        host.Start();
        var watch = Stopwatch.StartNew();
        int retiredPeriodic = 0;
        for (int cycle = 0; cycle < 32; cycle++)
        {
            if (cycle != 0)
            {
                var replacement = await host.ReloadAsync("Vega.Test.HotPlugin");
                Assert.That(replacement.Succeeded, Is.True, replacement.Message);
            }
            Assert.That(lifecycle.Subscribers, Is.EqualTo((2, 2)));
            lifecycle.Reconnect();
            await WaitUntilAsync(() => gameThread.Count("faulty-periodic") > retiredPeriodic);
            for (int fault = 0; fault < 5; fault++)
            {
                Assert.That((await Dispatch(dispatcher, "hotfault")).ErrorCode, Is.EqualTo("plugin.callback_failed"));
                Assert.That((await Dispatch(dispatcher, "healthyprobe")).Status, Is.EqualTo(CommandExecutionStatus.Success));
            }
            await WaitUntilAsync(() => host.SnapshotModules().Single(m => m.Name == "Vega.Test.HotPlugin").State == ModuleLoadState.Disabled);
            retiredPeriodic = gameThread.Count("faulty-periodic");
            int healthyPeriodic = gameThread.Count("healthy-periodic");
            lifecycle.Reconnect();
            await WaitUntilAsync(() => gameThread.Count("healthy-periodic") > healthyPeriodic);
            Assert.Multiple(() =>
            {
                Assert.That(dispatcher.CanDispatch("hotfault"), Is.False);
                Assert.That(host.SnapshotModules().Single(m => m.Name == "Vega.Test.HotPlugin").FaultCount, Is.EqualTo(5));
                Assert.That(host.SnapshotModules().Single(m => m.Name == "Vega.Test.HealthyPlugin").State, Is.EqualTo(ModuleLoadState.Loaded));
                Assert.That(host.Read().IsRestartRequired, Is.False);
                Assert.That(lifecycle.Subscribers, Is.EqualTo((1, 1)));
                Assert.That(gameThread.Count("faulty-joined"), Is.EqualTo(cycle + 1));
                Assert.That(gameThread.Count("faulty-left"), Is.EqualTo(cycle + 1));
                Assert.That(gameThread.Count("healthy-joined"), Is.EqualTo(2 * (cycle + 1)));
                Assert.That(gameThread.Count("healthy-left"), Is.EqualTo(2 * (cycle + 1)));
                Assert.That(gameThread.Count("faulty-periodic"), Is.EqualTo(retiredPeriodic));
            });
        }
        TestContext.Progress.WriteLine($"Independent plugin soak: 32 retirements, 160 isolated faults, 160 healthy replies, 64 reconnects, " +
            $"{gameThread.Count("faulty-periodic")} faulty/{gameThread.Count("healthy-periodic")} healthy scheduled callbacks in {watch.Elapsed.TotalSeconds:F2}s.");
    }

    [TestCase("hold-command")]
    [TestCase("hold-stop")]
    [TestCase("hold-stop-cancel")]
    public async Task NonCooperativePluginDoesNotBlockOtherPluginManagement(string gateName)
    {
        DeployTestPlugins();
        var dispatcher = new CommandDispatcher([]);
        using var host = new ServerPluginHotReloadHost(directory,
            new ServerPluginHotReloadOptions(true, 100, 500),
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));
        host.Start();
        string data = Path.Combine(directory, ".data", "Vega.Test.HotPlugin");
        Directory.CreateDirectory(data);
        string gate = Path.Combine(data, gateName);
        File.WriteAllText(gate, "1");
        Task<CommandExecutionResult>? blocked = null;
        try
        {
            if (gateName == "hold-command")
            {
                blocked = Dispatch(dispatcher, "hotstall").AsTask();
                await WaitUntilAsync(() => File.Exists(gate + ".entered"));
            }
            var watch = Stopwatch.StartNew();
            var unload = await host.UnloadAsync("Vega.Test.HotPlugin");
            Assert.Multiple(() =>
            {
                Assert.That(unload.Succeeded, Is.False);
                Assert.That(unload.RestartRequired, Is.True);
                Assert.That(unload.Message, Does.Contain("timeout"));
                Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
                Assert.That(host.SnapshotModules().Single(m => m.Name == "Vega.Test.HotPlugin").State, Is.EqualTo(ModuleLoadState.Degraded));
            });
            Assert.That((await Dispatch(dispatcher, "hotprobe")).Status, Is.Not.EqualTo(CommandExecutionStatus.Success));
            var reloadHealthy = await host.ReloadAsync("Vega.Test.HealthyPlugin");
            Assert.That(reloadHealthy.Succeeded, Is.True, reloadHealthy.Message);
            Assert.That((await Dispatch(dispatcher, "healthyprobe")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        }
        finally
        {
            File.Delete(gate);
            if (blocked is not null) await blocked;
            blocked = null;
        }
        var retry = await host.UnloadAsync("Vega.Test.HotPlugin");
        Assert.That(retry.Succeeded, Is.True, retry.Message);
        Assert.That(dispatcher.CanDispatch("hotprobe"), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task FailedActivationUsesBoundedRetirementEvenWithPendingIo(bool stallIo)
    {
        DeployTestPlugins();
        string data = Path.Combine(directory, ".data", "Vega.Test.HotPlugin");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "start-failure"), "1");
        string gate = Path.Combine(data, "hold-start-io");
        if (stallIo) File.WriteAllBytes(gate, []);
        var dispatcher = new CommandDispatcher([]);
        using var host = new ServerPluginHotReloadHost(directory,
            new ServerPluginHotReloadOptions(true, 100, 500),
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));
        try
        {
            var watch = Stopwatch.StartNew();
            host.Start();
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(host.SnapshotModules().Single(m => m.Name == "Vega.Test.HotPlugin").State,
                Is.EqualTo(stallIo ? ModuleLoadState.Degraded : ModuleLoadState.Failed));
            Assert.That(host.Read().IsRestartRequired, Is.EqualTo(stallIo));
            Assert.That((await Dispatch(dispatcher, "hotprobe")).Status, Is.Not.EqualTo(CommandExecutionStatus.Success));
            Assert.That((await Dispatch(dispatcher, "healthyprobe")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        }
        finally { File.Delete(gate); }
        if (stallIo)
        {
            var retry = await host.UnloadAsync("Vega.Test.HotPlugin");
            Assert.That(retry.Succeeded, Is.True, retry.Message);
        }
    }

    private void DeployTestPlugins()
    {
        foreach (string name in new[] { "Vega.HotReload.TestPlugin", "Vega.HotReload.HealthyTestPlugin" })
        {
            string path = FindTestPlugin(name);
            File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        }
    }

    [Test]
    public async Task DeclaredSdkGenerationMismatchIsRejectedBeforeConstructionAndKeepsExistingActivation()
    {
        string compatible = FindTestPlugin("Vega.HotReload.HealthyTestPlugin");
        string incompatible = FindTestPlugin("Vega.HotReload.IncompatibleTestPlugin");
        string deployed = Path.Combine(directory, Path.GetFileName(compatible));
        File.Copy(compatible, deployed);
        File.Copy(incompatible, Path.Combine(directory, Path.GetFileName(incompatible)));
        var dispatcher = new CommandDispatcher([]);
        using var host = new ServerPluginHotReloadHost(directory,
            new ServerPluginHotReloadOptions(true, 10000, 5000),
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));
        host.Start();
        Assert.Multiple(() =>
        {
            Assert.That(host.Read().Failure, Does.Contain($"declares SDK API generation {int.MaxValue}"));
            Assert.That(host.Read().Failure, Does.Not.Contain("constructor must not run"));
            Assert.That(host.SnapshotModules().Single().Name, Is.EqualTo("Vega.Test.HealthyPlugin"));
        });
        Assert.That((await Dispatch(dispatcher, "healthyprobe")).Status, Is.EqualTo(CommandExecutionStatus.Success));

        File.Copy(incompatible, deployed, overwrite: true);
        var replacement = await host.ReloadAsync("Vega.Test.HealthyPlugin");
        Assert.Multiple(() =>
        {
            Assert.That(replacement.Succeeded, Is.False);
            Assert.That(replacement.RestartRequired, Is.False);
            Assert.That(replacement.Message, Does.Contain($"supports generation {VegaPluginApiGenerationAttribute.Current}"));
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Loaded));
        });
        Assert.That((await Dispatch(dispatcher, "healthyprobe")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        var unload = await host.UnloadAsync("Vega.Test.HealthyPlugin");
        Assert.That(unload.Succeeded, Is.True, unload.Message);
        Assert.That(dispatcher.CanDispatch("healthyprobe"), Is.False);
    }

    private static ValueTask<CommandExecutionResult> Dispatch(CommandDispatcher dispatcher, string command) =>
        dispatcher.DispatchAsync(new CommandInvocation(command, [], new Principal(), new Output()));

    private sealed class LifecycleProbe : IPlayerSessionLifecycle
    {
        public event Action<PlayerSessionOpened>? Opened;
        public event Action<PlayerSessionClosed>? Closed;
        public (int, int) Subscribers => (Opened?.GetInvocationList().Length ?? 0, Closed?.GetInvocationList().Length ?? 0);
        public void Reconnect()
        {
            var session = PlayerSessionId.New();
            Opened?.Invoke(new(session, new(1), "soak player", DateTimeOffset.UtcNow));
            Closed?.Invoke(new(session, new(1), "soak player", DateTimeOffset.UtcNow));
        }
    }

    private sealed class ScheduledProbe : IGameThreadDispatcher
    {
        private readonly ConcurrentDictionary<string, int> calls = new();
        public int Count(string name) => calls.GetValueOrDefault(name);
        public bool IsGameThreadKnown => true;
        public bool IsGameThread => true;
        public void AssertGameThread() { }
        public bool TryPost(string name, Action action)
        {
            calls.AddOrUpdate(name, 1, static (_, count) => count + 1);
            action();
            return true;
        }
        public bool TrySchedule(string name, Func<bool> step) => throw new NotSupportedException();
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
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));
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
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));
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
            new ServerPluginHostServices(dispatcher, new RecordingLogger()));

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

    private static string FindTestPlugin(string name = "Vega.HotReload.TestPlugin")
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
            name,
            "bin",
            configuration,
            targetFramework,
            name + ".dll");
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
