from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one anchor, found {count}")
    return text.replace(old, new, 1)


scope_path = Path("src/Vega.Core/Modules/HotModuleRegistrationScope.cs")
scope = scope_path.read_text(encoding="utf-8")

scope = replace_once(
    scope,
    "    private readonly IOperationsLogger logger;\n    private readonly string logSource;\n",
    "    private readonly IOperationsLogger logger;\n    private readonly string logSource;\n    private readonly PluginExecutionGuard executionGuard;\n",
    "execution guard field",
)

scope = replace_once(
    scope,
    "        logSource = $\"plugin:{pluginName}\";\n\n        string configurationDirectory",
    "        logSource = $\"plugin:{pluginName}\";\n        executionGuard = new PluginExecutionGuard(logger, pluginName);\n\n        string configurationDirectory",
    "execution guard construction",
)

scope = replace_once(
    scope,
    "        public IDisposable RegisterCore(ICommandHandler handler) => commands.Register(handler);\n    }\n\n    private sealed class PluginPlayers",
    "        public IDisposable RegisterCore(ICommandHandler handler) =>\n            commands.Register(new GuardedCommandHandler(owner, handler));\n\n        private sealed class GuardedCommandHandler(\n            HotModuleRegistrationScope owner,\n            ICommandHandler inner) : ICommandHandler\n        {\n            public CommandDescriptor Descriptor => inner.Descriptor;\n\n            public ValueTask<CommandExecutionResult> ExecuteAsync(\n                CommandInvocation invocation,\n                CancellationToken cancellationToken) =>\n                owner.executionGuard.InvokeCommandAsync(\n                    $\"command:{Descriptor.Name}\",\n                    inner,\n                    invocation,\n                    cancellationToken);\n        }\n    }\n\n    private sealed class PluginPlayers",
    "command guard",
)

scope = replace_once(
    scope,
    "            lifecycle.Opened += handler;\n            owner.Track(new CleanupRegistration(() => lifecycle.Opened -= handler));",
    "            Action<PlayerSessionOpened> guarded = opened =>\n                owner.executionGuard.TryInvoke(\"players.joined\", () => handler(opened));\n            lifecycle.Opened += guarded;\n            owner.Track(new CleanupRegistration(() => lifecycle.Opened -= guarded));",
    "joined guard",
)

scope = replace_once(
    scope,
    "            lifecycle.Closed += handler;\n            owner.Track(new CleanupRegistration(() => lifecycle.Closed -= handler));",
    "            Action<PlayerSessionClosed> guarded = closed =>\n                owner.executionGuard.TryInvoke(\"players.left\", () => handler(closed));\n            lifecycle.Closed += guarded;\n            owner.Track(new CleanupRegistration(() => lifecycle.Closed -= guarded));",
    "left guard",
)

scope = replace_once(
    scope,
    "                if (!owner.lifetime.IsCancellationRequested)\n                {\n                    action();\n                }",
    "                if (!owner.lifetime.IsCancellationRequested)\n                {\n                    owner.executionGuard.TryInvoke($\"task:{name}\", action);\n                }",
    "scheduled callback guard",
)

scope_path.write_text(scope, encoding="utf-8")


test_path = Path("tests/Vega.Runtime.Tests/Core/PluginSdkContextTests.cs")
test = test_path.read_text(encoding="utf-8")

new_test = r'''
    [Test]
    public async Task PluginCallbackFaultsAreContainedAndAttributed()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(), "vega-plugin-sdk-fault-tests", Guid.NewGuid().ToString("N"));
        var commands = new RecordingCommandRegistry();
        var players = new RecordingPlayers();
        var dispatcher = new ImmediateDispatcher();
        var logger = new RecordingLogger();
        var scope = new HotModuleRegistrationScope(
            commands,
            logger,
            "Vega.Test.FaultyPlugin",
            dataDirectory,
            players,
            players,
            players,
            players,
            dispatcher);
        IVegaPluginContext plugin = scope;

        plugin.Commands.Register(new ThrowingCommand());
        plugin.Players.OnJoined(_ => throw new InvalidOperationException("join boom"));
        plugin.Players.OnLeft(_ => throw new InvalidOperationException("left boom"));

        Assert.DoesNotThrow(players.RaiseOpened);
        Assert.DoesNotThrow(players.RaiseClosed);
        Assert.That(
            () => plugin.Tasks.Post("throw-post", () => throw new InvalidOperationException("post boom")),
            Throws.Nothing);

        Assert.That(commands.Handler, Is.Not.Null);
        CommandExecutionResult command = await commands.Handler!.ExecuteAsync(
            new CommandInvocation("sdkboom", [], Principal.Instance, Output.Instance),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(command.Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(command.ErrorCode, Is.EqualTo("plugin.callback_failed"));
            Assert.That(logger.Entries, Has.Count.EqualTo(4));
            Assert.That(logger.Entries.All(entry => entry.Source == "plugin:Vega.Test.FaultyPlugin"), Is.True);
            Assert.That(logger.Entries.All(entry => entry.Level == OperationsLogLevel.Error), Is.True);
            Assert.That(logger.Entries.All(entry => entry.Exception is InvalidOperationException), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("players.joined", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("players.left", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("task:throw-post", StringComparison.Ordinal)), Is.True);
            Assert.That(logger.Entries.Any(entry => entry.Text.Contains("command:sdkboom", StringComparison.Ordinal)), Is.True);
        });

        await scope.DisposeAsync();
    }

'''

test = replace_once(
    test,
    "    private static async Task WaitUntilAsync(Func<bool> condition)\n",
    new_test + "    private static async Task WaitUntilAsync(Func<bool> condition)\n",
    "fault containment test insertion",
)

throwing_command = r'''
    private sealed class ThrowingCommand : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new("sdkboom", [], []);

        public ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("command boom");
    }

'''

test = replace_once(
    test,
    "    private sealed class RecordingCommandRegistry : ICommandRegistry\n",
    throwing_command + "    private sealed class RecordingCommandRegistry : ICommandRegistry\n",
    "throwing command insertion",
)

old_registry = r'''    private sealed class RecordingCommandRegistry : ICommandRegistry
    {
        public int Active { get; private set; }

        public IDisposable Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Active++;
            return new Registration(this);
        }

        private sealed class Registration(RecordingCommandRegistry owner) : IDisposable
        {
            private RecordingCommandRegistry? owner = owner;

            public void Dispose()
            {
                RecordingCommandRegistry? current = Interlocked.Exchange(ref owner, null);
                if (current is not null)
                {
                    current.Active--;
                }
            }
        }
    }
'''
new_registry = r'''    private sealed class RecordingCommandRegistry : ICommandRegistry
    {
        public int Active { get; private set; }
        public ICommandHandler? Handler { get; private set; }

        public IDisposable Register(ICommandHandler handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Active++;
            Handler = handler;
            return new Registration(this, handler);
        }

        private sealed class Registration(RecordingCommandRegistry owner, ICommandHandler handler) : IDisposable
        {
            private RecordingCommandRegistry? owner = owner;
            private ICommandHandler? handler = handler;

            public void Dispose()
            {
                RecordingCommandRegistry? current = Interlocked.Exchange(ref owner, null);
                ICommandHandler? currentHandler = Interlocked.Exchange(ref handler, null);
                if (current is not null)
                {
                    current.Active--;
                    if (ReferenceEquals(current.Handler, currentHandler))
                    {
                        current.Handler = null;
                    }
                }
            }
        }
    }
'''
test = replace_once(test, old_registry, new_registry, "recording command registry")

old_logger = r'''    private sealed class RecordingLogger : IOperationsLogger
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
'''
new_logger = r'''    private sealed class RecordingLogger : IOperationsLogger
    {
        private readonly List<LogEntry> entries = [];

        public IReadOnlyList<LogEntry> Entries => entries;

        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
            entries.Add(new LogEntry(level, source, text, exception));
        }
    }

    private sealed record LogEntry(
        OperationsLogLevel Level,
        string Source,
        string Text,
        Exception? Exception);

    private sealed class Principal : ICommandPrincipal
    {
        public static Principal Instance { get; } = new();
        public string Name => "server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class Output : ICommandOutput
    {
        public static Output Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
'''
test = replace_once(test, old_logger, new_logger, "recording logger")

test_path.write_text(test, encoding="utf-8")


roadmap_path = Path("docs/roadmap.md")
roadmap = roadmap_path.read_text(encoding="utf-8")
marker = "## Phase 26 — plugin fault containment and crash isolation"
if marker not in roadmap:
    phase = r'''
## Phase 26 — plugin fault containment and crash isolation

Contain ordinary same-process plugin failures at the Vega-owned PluginSdk boundary, preserve full diagnostics and retire repeatedly failing plugins without confusing exception suppression with authoritative-state recovery. Process-fatal failures and corrupted Terraria authoritative state remain outside the promise of same-process plugin containment.

### Callback boundary

- [ ] Route dynamically registered plugin commands through one per-plugin execution guard; thrown commands return a stable failure result and never escape the dispatcher.
- [ ] Route player join/leave and every future PluginSdk event callback through the same guard so one plugin cannot abort shared event delivery or Terraria snapshot refresh.
- [ ] Route `Tasks.Post`, periodic actions and async-result apply callbacks through the guard while preserving normal lifetime cancellation semantics.
- [ ] Log the complete exception object with plugin identity, callback/operation and a monotonic fault count instead of relying on outer generic catches.
- [ ] Add throwing-plugin regressions proving command, lifecycle and scheduled callback failures are contained and attributed.

### Circuit breaker and retirement

- [ ] Add a bounded rolling failure window and configurable breaker threshold with conservative defaults.
- [ ] Transition repeatedly failing plugins through degraded/faulted state, suppress new callbacks and retire only the offender.
- [ ] Cancel lifetime work, unregister hooks/commands, call stop/dispose and verify collectible `AssemblyLoadContext` reclamation; expose `RestartRequired` if cleanup cannot be proven.
- [ ] Keep a healthy neighboring plugin operational while a faulty plugin is being disabled.
- [ ] Surface fault count, last callback and last failure summary in module Operations/TUI state.

### Coverage and architecture gates

- [ ] Audit chat, packet, world, NPC, chest, tile, projectile, configuration and diagnostics callback surfaces for raw plugin delegate invocation.
- [ ] Add an architecture test that rejects new unguarded ordinary-plugin callback registrations outside the approved scope/guard boundary.
- [ ] Fault-injection soak: repeated plugin exceptions plus hot reload, reconnects and scheduled work must not leak handlers, tasks, threads or collectible load contexts.
- [ ] Document the boundary: `AssemblyLoadContext` is lifecycle isolation, not a hostile-code security sandbox; fatal CLR/native/process failures still require an external supervisor and last-known-good persistence.

Plugin-fault invariant:

```text
ordinary plugin exception = isolate + diagnose + possibly retire that plugin
authoritative state corruption = fail closed; never catch-and-continue blindly
```

'''
    roadmap = replace_once(
        roadmap,
        "\n## Architectural guardrails\n",
        "\n" + phase + "## Architectural guardrails\n",
        "roadmap phase insertion",
    )
    roadmap_path.write_text(roadmap, encoding="utf-8")

print("Crash containment v1 patch applied.")
