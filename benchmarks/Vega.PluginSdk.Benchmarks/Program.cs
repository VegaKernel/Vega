using System.Diagnostics;
using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Core.Modules;
using Vega.PluginSdk;

const int iterations = 5_000_000;
const int warmupIterations = 100_000;
var directRegistry = new RecordingRegistry();
var facadeRegistry = new RecordingRegistry();
var directLifecycle = new RecordingLifecycle();
var facadeLifecycle = new RecordingLifecycle();
var handler = new NoOpCommand();
var joinCount = 0;
var worldEvents = new WorldGameplayEventHub();
var scope = new HotModuleRegistrationScope(
    facadeRegistry,
    new NullLogger(),
    "Vega.Benchmarks.PluginSdk",
    Path.Combine(Path.GetTempPath(), "vega-plugin-sdk-benchmark"),
    playerLifecycle: facadeLifecycle, worldEvents: worldEvents);
IVegaPluginContext plugin = scope;
using IDisposable directRegistration = directRegistry.Register(handler);
plugin.Commands.Register(handler);
Action<PlayerSessionOpened> joined = _ => joinCount++;
directLifecycle.Opened += joined;
plugin.Players.OnJoined(joined);

if (!ReferenceEquals(handler, directRegistry.Handler) ||
    !ReferenceEquals(joined, directLifecycle.Handler) ||
    facadeRegistry.Handler is null || facadeLifecycle.Handler is null ||
    ReferenceEquals(handler, facadeRegistry.Handler) || ReferenceEquals(joined, facadeLifecycle.Handler))
{
    throw new InvalidOperationException("The plugin facade must register guarded command and lifecycle callbacks.");
}

var invocation = new CommandInvocation("noop", [], new Principal(), new Output());
ICommandHandler directCommandHandler = directRegistry.Handler!;
ICommandHandler facadeCommandHandler = facadeRegistry.Handler!;
var directCommand = MeasureCommand(directCommandHandler);
var facadeCommand = MeasureCommand(facadeCommandHandler);
var opened = new PlayerSessionOpened(PlayerSessionId.New(), new PlayerId(1), "player", DateTimeOffset.UtcNow);
var directHook = MeasureHook(directLifecycle);
var facadeHook = MeasureHook(facadeLifecycle);
var killed = new NpcKilledEvent(new(WorldId.Primary, 7, 1, 22, new(100, 100), 0, false, false));
var worldEventProbe = Measure(() => { if (worldEvents.HasSubscribers<NpcKilledEvent>()) worldEvents.Publish(killed); });
int npcCount = 0;
plugin.WorldEvents.OnNpcKilled(_ => npcCount++);
var worldEventDispatch = Measure(() => { if (worldEvents.HasSubscribers<NpcKilledEvent>()) worldEvents.Publish(killed); });
plugin.WorldEvents.AllowNpcTransform(_ => true);
var transform = new NpcTransformingEvent(killed.Npc, 23);
var worldPolicyDispatch = Measure(() => { if (worldEvents.HasPolicies<NpcTransformingEvent>() && !worldEvents.Allows(transform)) throw new InvalidOperationException(); });

if (joinCount != 2 * (iterations + warmupIterations) || npcCount != iterations + warmupIterations || scope.Faults.Count != 0 ||
    facadeCommand.AllocatedBytes > 4096 || facadeHook.AllocatedBytes > 4096 || worldEventProbe.AllocatedBytes > 4096 ||
    worldEventDispatch.AllocatedBytes > 4096 || worldPolicyDispatch.AllocatedBytes > 4096)
    throw new InvalidOperationException($"Guarded success path verification failed: joins={joinCount}, npc={npcCount}, faults={scope.Faults.Count}, " +
        $"allocated bytes: command={facadeCommand.AllocatedBytes}, join={facadeHook.AllocatedBytes}, empty event={worldEventProbe.AllocatedBytes}, " +
        $"event={worldEventDispatch.AllocatedBytes}, policy={worldPolicyDispatch.AllocatedBytes}.");

await scope.DisposeAsync();
if (worldEvents.HasSubscribers<NpcKilledEvent>() || worldEvents.HasPolicies<NpcTransformingEvent>())
    throw new InvalidOperationException("World callbacks were retained after plugin disposal.");
directLifecycle.Opened -= joined;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Runtime = Environment.Version.ToString(),
    Iterations = iterations,
    WarmupIterations = warmupIterations,
    Command = new { Direct = directCommand, Facade = facadeCommand },
    PlayerJoinHook = new { Direct = directHook, Facade = facadeHook },
    WorldEvent = new { EmptyProbe = worldEventProbe, GuardedDispatch = worldEventDispatch, GuardedPolicy = worldPolicyDispatch },
    DispatchWrapperInserted = true,
    GuardedSuccessPathsVerified = true,
    ManualLifetimeCallsRequired = 0,
    JoinCallbacksObserved = joinCount
}, new JsonSerializerOptions { WriteIndented = true }));

Measurement Measure(Action action)
{
    // All measured paths, including lifecycle/event/policy dispatch, must be warm
    // before steady-state allocation probes. Warmup callbacks are still counted.
    for (var index = 0; index < warmupIterations; index++) action();
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
