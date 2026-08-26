using NUnit.Framework;
using Vega.Contracts.Events;
using Vega.Contracts.Runtime;
using Vega.Core.Configuration;
using Vega.Core.Events;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ServerReloadTests
{
    private string directory = null!;
    private string path = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "vega-reload-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "server.json");
        File.WriteAllText(path, "{ \"port\": 7000 }");
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
    public async Task SuccessfulReloadDispatchesBeforeAndAfterAroundAtomicSwap()
    {
        var configuration = new VegaServerConfigurationState(path, Array.Empty<string>(), _ => null);
        var events = new AsyncEventHub();
        var order = new List<string>();
        using IDisposable before = events.Register(new Handler<ServerReloading>(context =>
        {
            order.Add("before");
            Assert.That(configuration.Current.Values["port"].DisplayValue, Is.EqualTo("7000"));
        }));
        using IDisposable after = events.Register(new Handler<ServerReloaded>(context =>
        {
            order.Add("after");
            Assert.That(configuration.Current.Values["port"].DisplayValue, Is.EqualTo("7001"));
        }));
        File.WriteAllText(path, "{ \"port\": 7001 }");
        var service = new ServerReloadService(configuration, events);

        ServerReloadResult result = await service.ReloadAsync("operator");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ServerReloadStatus.Succeeded));
            Assert.That(configuration.Current.Values["port"].DisplayValue, Is.EqualTo("7001"));
            Assert.That(order, Is.EqualTo(new[] { "before", "after" }));
        });
    }

    [Test]
    public async Task CancelledReloadDoesNotChangeCurrentOrDispatchReloaded()
    {
        var configuration = new VegaServerConfigurationState(path, Array.Empty<string>(), _ => null);
        var events = new AsyncEventHub();
        var afterCalled = false;
        using IDisposable before = events.Register(new Handler<ServerReloading>(context =>
            context.Cancel(new EventCancellation("test.cancelled", "blocked", "test"))));
        using IDisposable after = events.Register(new Handler<ServerReloaded>(_ => afterCalled = true));
        File.WriteAllText(path, "{ \"port\": 7001 }");
        var service = new ServerReloadService(configuration, events);

        ServerReloadResult result = await service.ReloadAsync("operator");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ServerReloadStatus.Cancelled));
            Assert.That(result.ErrorCode, Is.EqualTo("test.cancelled"));
            Assert.That(result.Message, Is.EqualTo("blocked"));
            Assert.That(configuration.Current.Values["port"].DisplayValue, Is.EqualTo("7000"));
            Assert.That(afterCalled, Is.False);
        });
    }

    [Test]
    public async Task InvalidConfigurationDoesNotDispatchReloadedOrReplaceCurrent()
    {
        var configuration = new VegaServerConfigurationState(path, Array.Empty<string>(), _ => null);
        var events = new AsyncEventHub();
        var afterCalled = false;
        using IDisposable after = events.Register(new Handler<ServerReloaded>(_ => afterCalled = true));
        File.WriteAllText(path, "{ \"port\": 0 }");
        var service = new ServerReloadService(configuration, events);

        ServerReloadResult result = await service.ReloadAsync("operator");

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(ServerReloadStatus.Failed));
            Assert.That(result.ErrorCode, Is.EqualTo("config.invalid"));
            Assert.That(configuration.Current.Values["port"].DisplayValue, Is.EqualTo("7000"));
            Assert.That(afterCalled, Is.False);
        });
    }

    [Test]
    public async Task DisposedDynamicRegistrationIsNotInvoked()
    {
        var events = new AsyncEventHub();
        var calls = 0;
        IDisposable registration = events.Register(new Handler<ProbeEvent>(_ => calls++));

        await events.DispatchAsync(new ProbeEvent());
        registration.Dispose();
        await events.DispatchAsync(new ProbeEvent());

        Assert.That(calls, Is.EqualTo(1));
    }

    private sealed record ProbeEvent;

    private sealed class Handler<TEvent>(Action<EventContext<TEvent>> callback) : IAsyncEventHandler<TEvent>
    {
        public ValueTask HandleAsync(EventContext<TEvent> context, CancellationToken cancellationToken)
        {
            callback(context);
            return ValueTask.CompletedTask;
        }
    }
}
