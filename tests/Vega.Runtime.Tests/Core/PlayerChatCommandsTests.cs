using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Messaging;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PlayerChatCommandsTests
{
    [Test]
    public async Task WhisperAndReplyStayInTheOriginalSessionAndRespectOptOut()
    {
        var fixture = new Fixture();
        using var chat = fixture.Start();
        Assert.That((await fixture.Run(fixture.Alice, "w", "Bob", "hello")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Deliveries.Select(d => d.Player), Is.EqualTo(new[] { fixture.Bob, fixture.Alice }));
        fixture.Deliveries.Clear();
        Assert.That((await fixture.Run(fixture.Bob, "r", "hello back")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Deliveries.Select(d => d.Player), Is.EqualTo(new[] { fixture.Alice, fixture.Bob }));
        await fixture.Run(fixture.Bob, "wa");
        fixture.Deliveries.Clear();
        Assert.That((await fixture.Run(fixture.Alice, "w", "Bob", "hello")).ErrorCode, Is.EqualTo("chat.whisper_blocked"));
        Assert.That(fixture.Deliveries, Is.Empty);
        PlayerSnapshot replacement = fixture.Bob with { SessionId = PlayerSessionId.New() };
        fixture.Replace(fixture.Bob, replacement);
        Assert.That((await fixture.Run(fixture.Alice, "r", "hello")).ErrorCode, Is.EqualTo("chat.no_reply"));
        Assert.That((await fixture.Run(fixture.Alice, "w", "Bob", "hello")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Deliveries.First().Player, Is.EqualTo(replacement));
    }

    [Test]
    public async Task PartyAndActionRespectWorldTeamAndModeration()
    {
        var fixture = new Fixture();
        using var chat = fixture.Start();
        Assert.That((await fixture.Run(fixture.Alice, "party", "team only")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Deliveries.Select(d => d.Player), Is.EquivalentTo(new[] { fixture.Alice, fixture.Bob }));
        fixture.Deliveries.Clear();
        Assert.That((await fixture.Run(fixture.Alice, "me", "waves")).Status, Is.EqualTo(CommandExecutionStatus.Success));
        Assert.That(fixture.Deliveries.Select(d => d.Player), Is.EquivalentTo(new[] { fixture.Alice, fixture.Bob, fixture.OtherTeam }));
        Assert.That(fixture.Deliveries.Select(d => d.Message), Is.All.EqualTo("* Alice waves"));
        fixture.Deliveries.Clear();
        fixture.Moderation.Muted = true;
        Assert.That((await fixture.Run(fixture.Alice, "w", "Bob", "muted")).ErrorCode, Is.EqualTo("chat.rejected"));
        Assert.That((await fixture.Run(fixture.Alice, "party", "muted")).ErrorCode, Is.EqualTo("chat.rejected"));
        Assert.That(fixture.Deliveries, Is.Empty);
        Assert.That((await fixture.Run(fixture.Alice, "w", "Remote", "message")).ErrorCode, Is.EqualTo("chat.target_not_unique"));
    }

    private sealed class Fixture : IPlayerDirectory, IPlayerMessenger, IPlayerSessionLifecycle
    {
        public PlayerSnapshot Alice { get; } = Player(1, "Alice", 1, "arena");
        public PlayerSnapshot Bob { get; } = Player(2, "Bob", 1, "arena");
        public PlayerSnapshot OtherTeam { get; } = Player(3, "Other", 2, "arena");
        private List<PlayerSnapshot> players = [];
        private readonly CommandDispatcher dispatcher = new([]);
        public ModerationRule Moderation { get; } = new();
        public List<(PlayerSnapshot Player, string Message)> Deliveries { get; } = [];
        public event Action<PlayerSessionOpened>? Opened { add { } remove { } }
        public event Action<PlayerSessionClosed>? Closed;

        public Fixture() => players = [Alice, Bob, OtherTeam, Player(4, "Remote", 1, "other")];
        public PlayerChatCommands Start() => new(dispatcher, this, this, this, new ChatPipeline(moderation: [Moderation]));
        public ValueTask<CommandExecutionResult> Run(PlayerSnapshot actor, string command, params string[] args) =>
            dispatcher.DispatchAsync(new(command, args, new Principal(actor), new Output()) { WorldId = actor.WorldId });
        public IReadOnlyList<PlayerSnapshot> SnapshotOnlinePlayers() => players;
        public bool TryGet(PlayerId id, out PlayerSnapshot? player) => (player = players.Find(p => p.Id == id)) is not null;
        public bool TryFindExact(string name, out PlayerSnapshot? player) => (player = players.Find(p => p.Name == name)) is not null;
        public void Replace(PlayerSnapshot old, PlayerSnapshot replacement)
        {
            players[players.IndexOf(old)] = replacement;
            Closed?.Invoke(new(old.SessionId!.Value, old.Id, old.Name, DateTimeOffset.UtcNow));
        }
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerId id, string message, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Chat must use session-bound delivery.");
        public ValueTask<PlayerOperationResult> SendMessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default)
        {
            Deliveries.Add((player, message));
            return ValueTask.FromResult(PlayerOperationResult.Applied());
        }
        private static PlayerSnapshot Player(int id, string name, int team, string world) =>
            new(new(id), name, false, 100, 100, 20, 20, new(0, 0), false, team)
            { WorldId = new(world), SessionId = PlayerSessionId.New() };
    }

    private sealed class ModerationRule : IChatModerationRule
    {
        public bool Muted { get; set; }
        public ChatModerationDecision Evaluate(ChatMessageContext context, string content) => Muted ? ChatModerationDecision.Reject("Muted") : ChatModerationDecision.Allow;
    }

    private sealed class Principal(PlayerSnapshot player) : IPlayerCommandPrincipal
    {
        public string Name => player.Name;
        public bool IsServer => false;
        public string CultureSubjectId => player.Name;
        public PlayerId PlayerId => player.Id;
        public PlayerSessionId? SessionId => player.SessionId;
        public string PlayerName => player.Name;
        public string? AccountName => null;
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
