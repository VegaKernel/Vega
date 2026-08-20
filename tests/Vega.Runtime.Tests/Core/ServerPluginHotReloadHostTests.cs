using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Core.Commands;
using Vega.Core.Modules;

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
    public async Task FileWatcherAutomaticallyLoadsAndUnloadsContractPlugin()
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
        await WaitUntilAsync(() => !dispatcher.CanDispatch("hotprobe"));

        Assert.Multiple(() =>
        {
            Assert.That(host.SnapshotModules().Single().State, Is.EqualTo(ModuleLoadState.Disabled));
            Assert.That(host.Read().ChangedFiles, Does.Contain("Watched.Plugin.dll"));
            Assert.That(host.Read().IsRestartRequired, Is.False);
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
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Vega.sln")))
        {
            current = current.Parent;
        }

        Assert.That(current, Is.Not.Null, "Repository root was not found from the test output directory.");
        string configuration = new DirectoryInfo(TestContext.CurrentContext.TestDirectory).Parent!.Name;
        string path = Path.Combine(
            current!.FullName,
            "tests",
            "Vega.HotReload.TestPlugin",
            "bin",
            configuration,
            "net10.0",
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
