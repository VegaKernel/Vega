using System.Net;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Moderation;
using Vega.Contracts.Players;

namespace Vega.Terraria.Commands;

internal static class TerrariaPrivateMessageCommandRegistration
{
    public static IDisposable Register(
        ICommandDispatcher dispatcher,
        IPlayerDirectory players,
        IPlayerMessenger messenger,
        IPlayerSessionLifecycle sessions,
        IMuteService mutes)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(messenger);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(mutes);

        if (dispatcher is not ICommandRegistry registry)
        {
            throw new InvalidOperationException(
                "The Vega command dispatcher must expose ICommandRegistry for private-message commands.");
        }

        var state = new PrivateMessageSessionState(sessions);
        IDisposable[] registrations =
        [
            registry.Register(new WhisperCommand(players, messenger, mutes, state)),
            registry.Register(new ReplyCommand(players, messenger, mutes, state)),
            registry.Register(new WallowCommand(players, state))
        ];
        return new RegistrationSet(state, registrations);
    }

    private sealed class RegistrationSet(
        PrivateMessageSessionState state,
        IDisposable[] registrations) : IDisposable
    {
        private PrivateMessageSessionState? ownedState = state;
        private IDisposable[]? ownedRegistrations = registrations;

        public void Dispose()
        {
            IDisposable[]? snapshot = Interlocked.Exchange(ref ownedRegistrations, null);
            if (snapshot is not null)
            {
                for (int index = snapshot.Length - 1; index >= 0; index--)
                {
                    snapshot[index].Dispose();
                }
            }

            Interlocked.Exchange(ref ownedState, null)?.Dispose();
        }
    }
}

internal sealed class PrivateMessageSessionState : IDisposable
{
    private readonly object sync = new();
    private readonly IPlayerSessionLifecycle sessions;
    private readonly Dictionary<PlayerSessionId, PlayerSessionId> lastCorrespondents = [];
    private readonly HashSet<PlayerSessionId> rejectingWhispers = [];
    private bool disposed;

    public PrivateMessageSessionState(IPlayerSessionLifecycle sessions)
    {
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        sessions.Closed += OnClosed;
    }

    public bool IsAccepting(PlayerSessionId sessionId)
    {
        lock (sync)
        {
            return !rejectingWhispers.Contains(sessionId);
        }
    }

    public bool ToggleAccepting(PlayerSessionId sessionId)
    {
        lock (sync)
        {
            if (rejectingWhispers.Remove(sessionId))
            {
                return true;
            }

            rejectingWhispers.Add(sessionId);
            return false;
        }
    }

    public void RecordConversation(PlayerSessionId first, PlayerSessionId second)
    {
        lock (sync)
        {
            lastCorrespondents[first] = second;
            lastCorrespondents[second] = first;
        }
    }

    public bool TryGetLastCorrespondent(PlayerSessionId sessionId, out PlayerSessionId correspondent)
    {
        lock (sync)
        {
            return lastCorrespondents.TryGetValue(sessionId, out correspondent);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        sessions.Closed -= OnClosed;
        lock (sync)
        {
            lastCorrespondents.Clear();
            rejectingWhispers.Clear();
        }
    }

    private void OnClosed(PlayerSessionClosed closed)
    {
        lock (sync)
        {
            rejectingWhispers.Remove(closed.SessionId);
            lastCorrespondents.Remove(closed.SessionId);
            foreach (PlayerSessionId owner in lastCorrespondents
                .Where(pair => pair.Value == closed.SessionId)
                .Select(static pair => pair.Key)
                .ToArray())
            {
                lastCorrespondents.Remove(owner);
            }
        }
    }
}

internal sealed class WhisperCommand(
    IPlayerDirectory players,
    IPlayerMessenger messenger,
    IMuteService mutes,
    PrivateMessageSessionState state) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "whisper",
        ["w", "tell", "pm", "dm"],
        ["vega.chat.whisper"],
        Syntax: "whisper <player> <message>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count < 2)
        {
            return Usage("Usage: whisper <player> <message>.");
        }

        if (!TryResolvePlayer(players, invocation.Arguments[0], out PlayerSnapshot? target, out string? error))
        {
            return Failure("command.whisper.player-not-found", error!);
        }

        IPlayerCommandPrincipal? callerPrincipal = invocation.Principal as IPlayerCommandPrincipal;
        PlayerSnapshot? caller = null;
        if (callerPrincipal is not null)
        {
            if (!players.TryGet(callerPrincipal.PlayerId, out caller) || caller is null || caller.SessionId is null)
            {
                return Failure("command.whisper.sender-unavailable", "Your player session is unavailable.");
            }

            if (target!.Id == caller.Id)
            {
                return Failure("command.whisper.self", "You cannot whisper to yourself.");
            }

            if (mutes.FindActive(BuildMuteIdentity(caller, callerPrincipal.AccountName), DateTimeOffset.UtcNow) is not null)
            {
                return Failure("command.whisper.muted", "You are muted.");
            }
        }

        if (target!.SessionId is not PlayerSessionId targetSession)
        {
            return Failure("command.whisper.target-unavailable", "The target player's session is unavailable.");
        }

        if (!state.IsAccepting(targetSession))
        {
            return Failure("command.whisper.not-accepting", $"{target.Name} is not accepting whispers.");
        }

        string senderName = caller?.Name ?? invocation.Principal.Name;
        string message = string.Join(" ", invocation.Arguments.Skip(1));
        PlayerOperationResult delivery = await messenger
            .SendMessageAsync(target.Id, $"<From {senderName}> {message}", cancellationToken)
            .ConfigureAwait(false);
        if (!delivery.IsSuccess)
        {
            return Failure(
                delivery.ErrorCode ?? "command.whisper.delivery-failed",
                $"Could not deliver the whisper to {target.Name}.");
        }

        if (caller?.SessionId is PlayerSessionId callerSession)
        {
            state.RecordConversation(callerSession, targetSession);
        }

        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.whisper.sent", target.Name, message)
                .WithDefaultText("<To {0}> {1}"));
    }

    private static CommandExecutionResult Usage(string text) => new(
        CommandExecutionStatus.InvalidUsage,
        "command.whisper.usage",
        [MessageDescriptor.Error("command.whisper.usage").WithDefaultText(text)]);

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));

    internal static bool TryResolvePlayer(
        IPlayerDirectory players,
        string raw,
        out PlayerSnapshot? player,
        out string? error)
    {
        player = null;
        error = null;
        string search = raw.Trim();
        if (search.Length == 0)
        {
            error = "Player name is empty.";
            return false;
        }

        PlayerSnapshot[] online = players.SnapshotOnlinePlayers().ToArray();
        if (int.TryParse(search, out int slot))
        {
            player = online.FirstOrDefault(candidate => candidate.Id.Slot == slot);
            if (player is not null)
            {
                return true;
            }
        }

        PlayerSnapshot[] matches = online
            .Where(candidate => candidate.Name.StartsWith(search, StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .ToArray();
        if (matches.Length == 1)
        {
            player = matches[0];
            return true;
        }

        error = matches.Length == 0
            ? $"Player '{search}' was not found."
            : $"Player name is ambiguous: {string.Join(", ", matches.Select(static match => match.Name))}.";
        return false;
    }

    internal static MuteIdentity BuildMuteIdentity(PlayerSnapshot player, string? accountName)
    {
        string? endpoint = player.Connection?.EffectiveEndpoint ?? player.RemoteEndpoint;
        string? ipAddress = null;
        if (IPAddress.TryParse(endpoint, out IPAddress? address))
        {
            ipAddress = NormalizeAddress(address).ToString();
        }
        else if (endpoint is not null && IPEndPoint.TryParse(endpoint, out IPEndPoint? parsed))
        {
            ipAddress = NormalizeAddress(parsed.Address).ToString();
        }

        return new MuteIdentity(player.Name, accountName, player.Connection?.ClientUuid, ipAddress);
    }

    private static IPAddress NormalizeAddress(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

internal sealed class ReplyCommand(
    IPlayerDirectory players,
    IPlayerMessenger messenger,
    IMuteService mutes,
    PrivateMessageSessionState state) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "reply",
        ["r"],
        ["vega.chat.whisper"],
        AllowServer: false,
        Syntax: "reply <message>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0 || invocation.Principal is not IPlayerCommandPrincipal principal)
        {
            return new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.reply.usage",
                [MessageDescriptor.Error("command.reply.usage").WithDefaultText("Usage: reply <message>.")]);
        }

        if (!players.TryGet(principal.PlayerId, out PlayerSnapshot? caller) ||
            caller?.SessionId is not PlayerSessionId callerSession)
        {
            return Failure("command.reply.sender-unavailable", "Your player session is unavailable.");
        }

        if (mutes.FindActive(WhisperCommand.BuildMuteIdentity(caller, principal.AccountName), DateTimeOffset.UtcNow) is not null)
        {
            return Failure("command.reply.muted", "You are muted.");
        }

        if (!state.TryGetLastCorrespondent(callerSession, out PlayerSessionId targetSession))
        {
            return Failure("command.reply.no-correspondent", "You have no active whisper correspondent.");
        }

        PlayerSnapshot? target = players.SnapshotOnlinePlayers()
            .FirstOrDefault(candidate => candidate.SessionId == targetSession);
        if (target is null)
        {
            return Failure("command.reply.offline", "Your last whisper correspondent is offline.");
        }

        if (!state.IsAccepting(targetSession))
        {
            return Failure("command.reply.not-accepting", $"{target.Name} is not accepting whispers.");
        }

        string message = string.Join(" ", invocation.Arguments);
        PlayerOperationResult delivery = await messenger
            .SendMessageAsync(target.Id, $"<From {caller.Name}> {message}", cancellationToken)
            .ConfigureAwait(false);
        if (!delivery.IsSuccess)
        {
            return Failure(
                delivery.ErrorCode ?? "command.reply.delivery-failed",
                $"Could not deliver the reply to {target.Name}.");
        }

        state.RecordConversation(callerSession, targetSession);
        return CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.reply.sent", target.Name, message)
                .WithDefaultText("<To {0}> {1}"));
    }

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

internal sealed class WallowCommand(
    IPlayerDirectory players,
    PrivateMessageSessionState state) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "wallow",
        ["wa"],
        ["vega.chat.whisper"],
        AllowServer: false,
        Syntax: "wallow");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 0 ||
            invocation.Principal is not IPlayerCommandPrincipal principal ||
            !players.TryGet(principal.PlayerId, out PlayerSnapshot? caller) ||
            caller?.SessionId is not PlayerSessionId sessionId)
        {
            return ValueTask.FromResult(new CommandExecutionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.wallow.usage",
                [MessageDescriptor.Error("command.wallow.usage").WithDefaultText("Usage: wallow.")]));
        }

        bool accepting = state.ToggleAccepting(sessionId);
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(
            MessageDescriptor.Info("command.wallow.changed", accepting)
                .WithDefaultText(accepting
                    ? "You may now receive whispers from other players."
                    : "You will no longer receive whispers from other players.")));
    }
}
