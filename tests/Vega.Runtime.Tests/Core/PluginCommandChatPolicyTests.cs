using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Messaging;
using Vega.Core.Modules;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginCommandChatPolicyTests
{
    [Test]
    public async Task CommandVetoOnlyRunsAfterAuthorizationAndForExactWorldSessionAndRetires()
    {
        PlayerSnapshot player = Player();
        var players = new Players { Current = player };
        var command = new ProbeCommand("probe");
        var commands = new CommandDispatcher([command]);
        var principal = new Principal(player);
        await using var scope = new HotModuleRegistrationScope(commands, new Logger(), "Policy.Commands", Path.GetTempPath(),
            playerDirectory: players, activityWorldId: player.WorldId);
        Assert.That(scope.Activity.PlayerCommandPolicyAvailable, Is.True);
        Assert.That(scope.Commands.CatalogAvailable, Is.True);
        scope.Commands.Register(new ProbeCommand("pluginprobe"));
        var catalog = scope.Commands.Snapshot();
        Assert.That(catalog.Select(descriptor => descriptor.Name), Is.EquivalentTo(new[] { "probe", "pluginprobe" }));
        var descriptor = catalog.Single(entry => entry.Name == "probe");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)descriptor.Aliases)[0] = "changed");

        var observed = new List<PlayerCommandPolicyContext>();
        scope.Activity.BeforePlayerCommand(_ => throw new InvalidOperationException("fixture failure"));
        scope.Activity.BeforePlayerCommand(value =>
        {
            observed.Add(value);
            return CommandPolicyDecision.Reject("Not available.");
        });
        var invocation = new CommandInvocation("probe-alias", ["not visible to policy"], principal, Output.Instance) { WorldId = player.WorldId };
        Assert.That((await commands.DispatchAsync(invocation)).ErrorCode, Is.EqualTo("command.permission_denied"));
        Assert.That(observed, Is.Empty);
        principal.Authorized = true;
        Assert.That((await commands.DispatchAsync(invocation)).ErrorCode, Is.EqualTo("command.policy_rejected"));
        Assert.That(command.Executions, Is.Zero);
        Assert.That(observed.Single(), Is.EqualTo(new PlayerCommandPolicyContext(player.WorldId, "probe", "probe-alias", player.Id, player.SessionId!.Value)));
        Assert.That(scope.Faults.Count, Is.EqualTo(1));
        // Neither stale player tenures nor a foreign world reaches this plugin's policy.
        players.Current = player with { SessionId = PlayerSessionId.New() };
        Assert.That((await commands.DispatchAsync(invocation)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        players.Current = player;
        Assert.That((await commands.DispatchAsync(invocation with { WorldId = new WorldId("other") })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That((await commands.DispatchAsync(invocation with { Principal = ServerPrincipal.Instance })).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(observed, Has.Count.EqualTo(1));
        await scope.DisposeAsync();
        Assert.That(commands.CanDispatch("pluginprobe"), Is.False);
        Assert.That((await commands.DispatchAsync(invocation)).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(observed, Has.Count.EqualTo(1));

        // A pre-handler cannot make an earlier permission check survive revocation.
        await using var revokingScope = new HotModuleRegistrationScope(commands, new Logger(), "Policy.Revocation", Path.GetTempPath(),
            playerDirectory: players, activityWorldId: player.WorldId);
        revokingScope.Activity.BeforePlayerCommand(_ => { principal.Authorized = false; return CommandPolicyDecision.Allow; });
        int executions = command.Executions;
        Assert.That((await commands.DispatchAsync(invocation)).ErrorCode, Is.EqualTo("command.permission_denied"));
        Assert.That(command.Executions, Is.EqualTo(executions));
    }

    [Test]
    public async Task PublicChatReplacementAndVetoCannotOverrideHostModerationAndRetire()
    {
        PlayerSnapshot player = Player();
        var players = new Players { Current = player };
        var chat = new ChatPipeline(moderation: [new RejectBlocked()]);
        await using var scope = new HotModuleRegistrationScope(new CommandDispatcher([]), new Logger(), "Policy.Chat", Path.GetTempPath(),
            playerDirectory: players, activityWorldId: player.WorldId, publicChat: chat);
        Assert.That(scope.Activity.PublicChatPolicyAvailable, Is.True);
        scope.Activity.BeforePublicChat(value => value.Content == "fault" ? throw new InvalidOperationException("fixture failure") : PublicChatPolicyDecision.Allow);
        scope.Activity.BeforePublicChat(value => value.Content switch
        {
            "hello" => PublicChatPolicyDecision.Replace("edited"),
            "bad" => PublicChatPolicyDecision.Replace("blocked"),
            "invalid" => PublicChatPolicyDecision.Replace("/invalid"),
            "stop" => PublicChatPolicyDecision.Reject("Please stop."),
            _ => PublicChatPolicyDecision.Allow
        });
        var observed = new List<PublicChatPolicyContext>();
        scope.Activity.BeforePublicChat(value => { observed.Add(value); return PublicChatPolicyDecision.Allow; });

        ChatPipelineResult Process(string content, WorldId? world = null, PlayerSnapshot? sender = null) =>
            chat.Process(new ChatMessageContext(world ?? player.WorldId, sender ?? player, content, [player]));
        var edited = Process("hello");
        Assert.That(edited.RequiresCustomDelivery, Is.True);
        Assert.That(edited.PublicContent, Is.EqualTo("edited"));
        Assert.That(observed.Single().Content, Is.EqualTo("edited"));
        Assert.That(edited.Deliveries.Single().Content, Is.EqualTo($"{player.Name}: edited"));
        Assert.That(Process("bad").RejectionReason, Is.EqualTo("host-rejected"));
        Assert.That(Process("stop").RejectionReason, Is.EqualTo("Please stop."));
        Assert.That(Process("fault").PublicContent, Is.EqualTo("fault"));
        Assert.That(Process("invalid").PublicContent, Is.EqualTo("invalid"));
        Assert.That(scope.Faults.Count, Is.EqualTo(2));
        int callbacks = observed.Count;
        Assert.That(Process("/unknown").PublicContent, Is.EqualTo("/unknown"));
        Assert.That(Process("hello", new WorldId("other")).PublicContent, Is.EqualTo("hello"));
        Assert.That(Process("hello", sender: player with { SessionId = PlayerSessionId.New() }).PublicContent, Is.EqualTo("hello"));
        Assert.That(observed, Has.Count.EqualTo(callbacks));
        await scope.DisposeAsync();
        Assert.That(Process("hello").PublicContent, Is.EqualTo("hello"));
        Assert.That(observed, Has.Count.EqualTo(callbacks));
    }

    private static PlayerSnapshot Player() => new(new(1), "fixture", false, 100, 100, 20, 20, new(0, 0), false, 0)
        { WorldId = new WorldId("fixture"), SessionId = PlayerSessionId.New() };

    private sealed class Players : IPlayerDirectory
    {
        public PlayerSnapshot? Current { get; set; }
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => Current is { } value ? [value] : [];
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) { player = Current?.Id == id ? Current : null; return player is not null; }
        public bool TryFindExact(string name, out PlayerSnapshot? player) { player = Current?.Name == name ? Current : null; return player is not null; }
    }

    private sealed class Principal(PlayerSnapshot player) : IPlayerCommandPrincipal
    {
        public string Name => player.Name;
        public string PlayerName => player.Name;
        public string? AccountName => null;
        public PlayerId PlayerId => player.Id;
        public PlayerSessionId? SessionId => player.SessionId;
        public string CultureSubjectId => "fixture";
        public bool IsServer => false;
        public bool Authorized { get; set; }
        public bool HasPermission(string permission) => Authorized;
    }

    private sealed class ServerPrincipal : ICommandPrincipal
    {
        public static ServerPrincipal Instance { get; } = new();
        public string Name => "Server";
        public bool IsServer => true;
        public bool HasPermission(string permission) => true;
    }

    private sealed class ProbeCommand(string name) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(name, [$"{name}-alias"], ["fixture.permission"]);
        public int Executions { get; private set; }
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
        { Executions++; return ValueTask.FromResult(CommandExecutionResult.Success); }
    }

    private sealed class RejectBlocked : IChatModerationRule
    {
        public ChatModerationDecision Evaluate(ChatMessageContext context, string content) =>
            content == "blocked" ? ChatModerationDecision.Reject("host-rejected") : ChatModerationDecision.Allow;
    }

    private sealed class Logger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }

    private sealed class Output : ICommandOutput
    {
        public static Output Instance { get; } = new();
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
