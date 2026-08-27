using System.Diagnostics;
using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Core.Modules;
using Vega.PluginSdk;

const int iterations = 5_000_000;
var directRegistry = new RecordingRegistry();
var facadeRegistry = new RecordingRegistry();
var callbackRegistry = new RecordingRegistry();
var directLifecycle = new RecordingLifecycle();
var facadeLifecycle = new RecordingLifecycle();
var handler = new NoOpCommand();
var joinCount = 0;
var scope = new HotModuleRegistrationScope(
    facadeRegistry,
    new NullLogger(),
    "Vega.Benchmarks.PluginSdk",
    Path.Combine(Path.GetTempPath(), "vega-plugin-sdk-benchmark"),
    playerLifecycle: facadeLifecycle);
var callbackScope = new HotModuleRegistrationScope(
    callbackRegistry,
    new NullLogger(),
    "Vega.Benchmarks.PluginSdk.Callback",
    Path.Combine(Path.GetTempPath(), "vega-plugin-sdk-callback-benchmark"));
IVegaPluginContext plugin = scope;
IVegaPluginContext callbackPlugin = callbackScope;
using IDisposable directRegistration = directRegistry.Register(handler);
plugin.Commands.Register(handler);
callbackPlugin.Commands.Register(
    "noop",
    (_, _) => ValueTask.FromResult(CommandExecutionResult.Success));
Action<PlayerSessionOpened> joined = _ => joinCount++;
directLifecycle.Opened += joined;
plugin.Players.OnJoined(joined);

if (!ReferenceEquals(handler, directRegistry.Handler) ||
    !ReferenceEquals(handler, facadeRegistry.Handler) ||
    !ReferenceEquals(joined, directLifecycle.Handler) ||
    !ReferenceEquals(joined, facadeLifecycle.Handler))
{
    throw new InvalidOperationException("The explicit plugin facade inserted a dispatch wrapper.");
}

if (callbackRegistry.Handler is null || callbackRegistry.Handler.Descriptor.Name != "noop")
{
    throw new InvalidOperationException("The callback plugin command was not adapted into the normal command registry.");
}

var invocation = new CommandInvocation("noop", [], new Principal(), new Output());
ICommandHandler directCommandHandler = directRegistry.Handler!;
ICommandHandler facadeCommandHandler = facadeRegistry.Handler!;
ICommandHandler callbackCommandHandler = callbackRegistry.Handler!;
for (var index = 0; index < 100_000; index++)
{
    directCommandHandler.ExecuteAsync(invocation, CancellationToken.None).GetAwaiter().GetResult();
    facadeCommandHandler.ExecuteAsync(invocation, CancellationToken.None).GetAwaiter().GetResult();
    callbackCommandHandler.ExecuteAsync(invocation, CancellationToken.None).GetAwaiter().GetResult();
}

var directCommand = MeasureCommand(directCommandHandler);
var facadeCommand = MeasureCommand(facadeCommandHandler);
var callbackCommand = MeasureCommand(callbackCommandHandler);
var opened = new PlayerSessionOpened(PlayerSessionId.New(), new PlayerId(1), "player", DateTimeOffset.UtcNow);
var directHook = MeasureHook(directLifecycle);
var facadeHook = MeasureHook(facadeLifecycle);

await callbackScope.DisposeAsync();
await scope.DisposeAsync();
directLifecycle.Opened -= joined;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Runtime = Environment.Version.ToString(),
    Iterations = iterations,
    Command = new { Direct = directCommand, Facade = facadeCommand, Callback = callbackCommand },
    PlayerJoinHook = new { Direct = directHook, Facade = facadeHook },
    ExplicitDispatchWrapperInserted = false,
    CallbackWrapperInserted = true,
    ManualLifetimeCallsRequired = 0,
    JoinCallbacksObserved = joinCount
}, new JsonSerializerOptions { WriteIndented = true }));

Measurement Measure(Action action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    for (var index = 0; index < iterations; index++)
    {
        action();
    }

    return new Measurement(
        Stopwatch.GetElapsedTime(started).TotalNanoseconds / iterations,
        GC.GetAllocatedBytesForCurrentThread() - allocated);
}

Measurement MeasureCommand(ICommandHandler target) => Measure(() =>
    target.ExecuteAsync(invocation, CancellationToken.None).GetAwaiter().GetResult());

Measurement MeasureHook(RecordingLifecycle target) => Measure(() => target.Raise(opened));

readonly record struct Measurement(double NanosecondsPerCall, long AllocatedBytes);

sealed class RecordingRegistry : ICommandRegistry
{
    public ICommandHandler? Handler { get; private set; }

    public IDisposable Register(ICommandHandler handler)
    {
        Handler = handler;
        return new Registration(this);
    }

    private sealed class Registration(RecordingRegistry owner) : IDisposable
    {
        public void Dispose() => owner.Handler = null;
    }
}

sealed class RecordingLifecycle : IPlayerSessionLifecycle
{
    public event Action<PlayerSessionOpened>? Opened;
    public event Action<PlayerSessionClosed>? Closed
    {
        add { }
        remove { }
    }

    public Action<PlayerSessionOpened>? Handler => Opened;
    public void Raise(PlayerSessionOpened opened) => Opened?.Invoke(opened);
}

sealed class NoOpCommand : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("noop", [], []);

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken) => ValueTask.FromResult(CommandExecutionResult.Success);
}

sealed class Principal : ICommandPrincipal
{
    public string Name => "benchmark";
    public bool IsServer => true;
    public bool HasPermission(string permission) => true;
}

sealed class Output : ICommandOutput
{
    public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

sealed class NullLogger : IOperationsLogger
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
