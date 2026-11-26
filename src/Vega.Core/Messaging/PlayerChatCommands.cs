using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.Core.Messaging;

/// <summary>Explicit recipient audiences for TShock-style private and team chat.</summary>
public sealed class PlayerChatCommands : IDisposable
{
    private readonly IPlayerDirectory players;
    private readonly IPlayerMessenger messenger;
    private readonly IPlayerSessionLifecycle sessions;
    private readonly IChatPipeline chat;
    private readonly object sync = new();
    private readonly Dictionary<PlayerSessionId, PlayerSnapshot> partners = [];
    private readonly HashSet<PlayerSessionId> blocked = [];
    private readonly List<IDisposable> registrations = [];

    public PlayerChatCommands(ICommandRegistry registry, IPlayerDirectory players, IPlayerMessenger messenger,
        IPlayerSessionLifecycle sessions, IChatPipeline chat)
    {
        this.players = players;
        this.messenger = messenger;
        this.sessions = sessions;
        this.chat = chat;
        try
        {
            Register("whisper", ["w", "tell", "pm", "dm"], "vega.chat.whisper", "whisper <player> <message>", WhisperAsync);
            Register("reply", ["r"], "vega.chat.whisper", "reply <message>", ReplyAsync);
            Register("wallow", ["wa"], "vega.chat.whisper", "wallow", ToggleAsync);
            Register("party", ["p"], "vega.chat.party", "party <message>", PartyAsync);
            Register("me", [], "vega.chat.me", "me <action>", ActionAsync);
            sessions.Closed += OnClosed;
        }
        catch { Dispose(); throw; }

        void Register(string name, string[] aliases, string permission, string syntax,
            Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> execute) =>
            registrations.Add(registry.Register(new Handler(new(name, aliases, [permission], AllowServer: false, Syntax: syntax), execute)));
    }

    private async ValueTask<CommandExecutionResult> WhisperAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count < 2) return Usage();
        if (Self(invocation) is not { } sender) return Unavailable();
        var candidates = players.SnapshotOnlinePlayers().Where(p => p.WorldId == sender.WorldId && p.SessionId.HasValue).ToArray();
        var matches = candidates.Where(p => p.Name.Equals(invocation.Arguments[0], StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) matches = candidates.Where(p => p.Name.StartsWith(invocation.Arguments[0], StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].SessionId == sender.SessionId) return Failure("chat.target_not_unique", "Choose one other online player.");
        return await PrivateAsync(sender, matches[0], string.Join(' ', invocation.Arguments.Skip(1)), token).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> ReplyAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count == 0) return Usage();
        if (Self(invocation) is not { SessionId: { } session } sender) return Unavailable();
        PlayerSnapshot? partner;
        lock (sync) partners.TryGetValue(session, out partner);
        return partner is null ? Failure("chat.no_reply", "There is no active whisper conversation.") :
            await PrivateAsync(sender, partner, string.Join(' ', invocation.Arguments), token).ConfigureAwait(false);
    }

    private async ValueTask<CommandExecutionResult> PrivateAsync(PlayerSnapshot sender, PlayerSnapshot partner, string content, CancellationToken token)
    {
        if (!Valid(content)) return Usage();
        if (!IsCurrent(sender) || !IsCurrent(partner) || sender.WorldId != partner.WorldId ||
            sender.SessionId is not { } from || partner.SessionId is not { } to) return Unavailable();
        lock (sync)
            if (blocked.Contains(to)) return Failure("chat.whisper_blocked", "This player is not accepting whispers.");
        var result = chat.Process(new(sender.WorldId, sender, content, [partner]));
        if (!result.Allowed) return Failure("chat.rejected", result.RejectionReason ?? "Chat rejected.");
        if (result.Deliveries.SingleOrDefault(d => d.RecipientId == partner.Id) is not { } delivery ||
            !(await messenger.SendMessageAsync(partner, $"[Whisper] {delivery.Content}", token).ConfigureAwait(false)).IsSuccess) return Unavailable();
        lock (sync)
        {
            if (IsCurrent(sender) && IsCurrent(partner))
            {
                partners[from] = partner;
                partners[to] = sender;
            }
        }
        await messenger.SendMessageAsync(sender, $"[Whisper to {partner.Name}] {content}", token).ConfigureAwait(false);
        return new(CommandExecutionStatus.Success);
    }

    private ValueTask<CommandExecutionResult> ToggleAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 0) return ValueTask.FromResult(Usage());
        if (Self(invocation) is not { SessionId: { } session }) return ValueTask.FromResult(Unavailable());
        bool enabled;
        lock (sync) { enabled = blocked.Remove(session); if (!enabled) blocked.Add(session); }
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Info("chat.whispers", enabled)
            .WithDefaultText("Accepting whispers: {0}.")));
    }

    private ValueTask<CommandExecutionResult> PartyAsync(CommandInvocation invocation, CancellationToken token) => SendAudienceAsync(invocation, true, token);
    private ValueTask<CommandExecutionResult> ActionAsync(CommandInvocation invocation, CancellationToken token) => SendAudienceAsync(invocation, false, token);

    private async ValueTask<CommandExecutionResult> SendAudienceAsync(CommandInvocation invocation, bool party, CancellationToken token)
    {
        string content = string.Join(' ', invocation.Arguments);
        if (!Valid(content)) return Usage();
        if (Self(invocation) is not { } sender) return Unavailable();
        if (party && sender.Team == 0) return Failure("chat.no_team", "Join a team first.");
        var audience = players.SnapshotOnlinePlayers().Where(p => p.SessionId.HasValue && p.WorldId == sender.WorldId &&
            (!party || p.Team == sender.Team)).ToArray();
        var result = chat.Process(new(sender.WorldId, sender, content, audience));
        if (!result.Allowed) return Failure("chat.rejected", result.RejectionReason ?? "Chat rejected.");
        var byId = audience.ToDictionary(p => p.Id);
        foreach (var delivery in result.Deliveries)
        {
            if (!IsCurrent(sender) || party && (!players.TryGet(sender.Id, out var currentSender) || currentSender?.Team != sender.Team))
                return Unavailable();
            if (!byId.TryGetValue(delivery.RecipientId, out var recipient) || !IsCurrent(recipient) ||
                party && (!players.TryGet(recipient.Id, out var current) || current?.Team != sender.Team)) continue;
            string prefix = $"{sender.Name}: ";
            string message = party ? $"[Party] {delivery.Content}" : delivery.Content.StartsWith(prefix, StringComparison.Ordinal)
                ? $"* {sender.Name} {delivery.Content[prefix.Length..]}" : $"* {delivery.Content}";
            await messenger.SendMessageAsync(recipient, message, token).ConfigureAwait(false);
        }
        return new(CommandExecutionStatus.Success);
    }

    private PlayerSnapshot? Self(CommandInvocation invocation) => invocation.Principal is IPlayerCommandPrincipal { SessionId: { } session } principal &&
        players.TryGet(principal.PlayerId, out var player) && player?.SessionId == session && player.WorldId == invocation.WorldId ? player : null;

    private bool IsCurrent(PlayerSnapshot expected) => players.TryGet(expected.Id, out var actual) && actual is not null &&
        actual.SessionId == expected.SessionId && actual.WorldId == expected.WorldId;
    private static bool Valid(string content) => !string.IsNullOrWhiteSpace(content) && content.Length <= 512;
    private static CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "chat.usage");
    private static CommandExecutionResult Unavailable() => Failure("chat.player_unavailable", "Player is no longer available.");
    private static CommandExecutionResult Failure(string code, string text) => CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));

    private void OnClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            blocked.Remove(closed.SessionId);
            partners.Remove(closed.SessionId);
            foreach (var key in partners.Where(p => p.Value.SessionId == closed.SessionId).Select(p => p.Key).ToArray()) partners.Remove(key);
        }
    }

    public void Dispose()
    {
        sessions.Closed -= OnClosed;
        foreach (var registration in registrations) registration.Dispose();
        registrations.Clear();
        lock (sync) { blocked.Clear(); partners.Clear(); }
    }

    private sealed class Handler(CommandDescriptor descriptor, Func<CommandInvocation, CancellationToken, ValueTask<CommandExecutionResult>> execute) : ICommandHandler
    {
        public CommandDescriptor Descriptor => descriptor;
        public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken) => execute(invocation, cancellationToken);
    }
}
