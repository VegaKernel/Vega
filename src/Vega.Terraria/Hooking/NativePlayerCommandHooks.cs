using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using OTAPI;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Operations;
using Vega.Contracts.Moderation;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Routes Vega-owned player chat commands through the command dispatcher.
/// Non-command chat and unknown commands remain available to Terraria's normal chat path.
/// </summary>
internal sealed class NativePlayerCommandHooks : IDisposable
{
    private readonly ICommandDispatcher dispatcher;
    private readonly IPlayerAuthorizationService authorization;
    private readonly IPlayerDirectory players;
    private readonly IPlayerMessenger messenger;
    private readonly IOperationsLogger logger;
    private readonly ILocalizationService localization;
    private readonly IPlayerCultureService cultures;
    private readonly IPlayerSessionLifecycle sessions;
    private readonly IPlayerAccountSessionService accountSessions;
    private readonly IMuteService? mutes;
    private readonly TimeProvider time;
    private bool attached;

    public NativePlayerCommandHooks(
        ICommandDispatcher dispatcher,
        IPlayerAuthorizationService authorization,
        IPlayerDirectory players,
        IPlayerMessenger messenger,
        IOperationsLogger logger,
        ILocalizationService localization,
        IPlayerCultureService cultures,
        IPlayerSessionLifecycle sessions,
        IPlayerAccountSessionService accountSessions,
        IMuteService? mutes = null,
        TimeProvider? timeProvider = null)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));
        this.cultures = cultures ?? throw new ArgumentNullException(nameof(cultures));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.accountSessions = accountSessions ?? throw new ArgumentNullException(nameof(accountSessions));
        this.mutes = mutes;
        time = timeProvider ?? TimeProvider.System;
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData += OnReceiveData;
        sessions.Closed += OnSessionClosed;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData -= OnReceiveData;
        sessions.Closed -= OnSessionClosed;
        attached = false;
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel || args.PacketId != (byte)PacketTypes.LoadNetModule)
        {
            return;
        }

        if (!TryGetPlayerCommand(args, out string messageText) || messageText.Length == 0)
        {
            return;
        }

        var playerId = new PlayerId(args.Instance.whoAmI);
        if (!players.TryGet(playerId, out PlayerSnapshot? player) || player is null)
        {
            return;
        }

        bool looksLikeCommand = messageText.Length >= 2 &&
            (messageText[0] == '/' || messageText[0] == '.');
        if (looksLikeCommand &&
            CommandTextParser.TryParse(messageText, out ParsedCommandText command) &&
            dispatcher.CanDispatch(command.Name))
        {
            // This synchronous OTAPI hook only claims commands already owned by Vega.
            // Execution and game-thread message delivery continue asynchronously without blocking packet processing.
            args.Result = HookResult.Cancel;
            _ = ExecuteAsync(playerId, player.Name, command);
            return;
        }

        accountSessions.TryGetAccount(playerId, out AccountSnapshot? chatAccount);
        MuteSnapshot? mute = mutes?.FindActive(player.Name, chatAccount?.Name, time.GetUtcNow());
        if (mute is not null)
        {
            args.Result = HookResult.Cancel;
            _ = NotifyMutedAsync(playerId, mute);
            logger.Log(
                OperationsLogLevel.Information,
                "moderation",
                $"Muted chat rejected for {player.Name}; mute={mute.Id}.",
                correlationId: player.SessionId?.ToString());
            return;
        }

        logger.Log(
            OperationsLogLevel.Information,
            "chat",
            $"{player.Name}: {messageText}",
            correlationId: player.SessionId?.ToString());
    }

    private static bool TryGetPlayerCommand(
        Hooks.MessageBuffer.GetDataEventArgs args,
        out string commandText)
    {
        commandText = string.Empty;

        if (!PacketViewParser.TryParsePayloadBounded(
                args.PacketId,
                args.Instance.readBuffer,
                args.ReadOffset,
                args.Length,
                out PacketView packet))
        {
            return false;
        }

        try
        {
            var module = new LoadNetModuleView(packet);
            if (module.ModuleType != NetworkModuleTypes.NetTextModule)
            {
                return false;
            }

            var textModule = new NetTextModuleView(module);
            if (textModule.PayloadKind != NetTextModulePayloadKind.ClientChatMessage)
            {
                return false;
            }

            commandText = textModule.ChatMessage;
            return true;
        }
        catch (Exception)
        {
            // Malformed or unsupported chat payloads remain available to Terraria's normal chat path.
            return false;
        }
    }

    private async Task ExecuteAsync(
        PlayerId playerId,
        string playerName,
        ParsedCommandText command)
    {
        try
        {
            var output = new PlayerCommandOutput(messenger, playerId);
            accountSessions.TryGetAccount(playerId, out AccountSnapshot? account);
            var invocation = new CommandInvocation(
                command.Name,
                command.Arguments,
                new PlayerCommandPrincipal(playerName, playerId, authorization, account),
                output,
                command.Silent);

            CommandExecutionResult result = await dispatcher
                .DispatchAsync(invocation)
                .ConfigureAwait(false);

            await WriteResultAsync(output, result, playerId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Vega player command '{command.Name}' failed for slot {playerId.Slot}: {ex}");
        }
    }

    private async ValueTask WriteResultAsync(
        ICommandOutput output,
        CommandExecutionResult result,
        PlayerId playerId)
    {
        if (result.Messages is { Count: > 0 })
        {
            foreach (MessageDescriptor message in result.Messages)
            {
                string text = localization.Format(message, cultures.Resolve(CultureSubject(playerId)));
                switch (message.Severity)
                {
                    case MessageSeverity.Success:
                        await output.SuccessAsync(text).ConfigureAwait(false);
                        break;
                    case MessageSeverity.Warning:
                        await output.WarningAsync(text).ConfigureAwait(false);
                        break;
                    case MessageSeverity.Error:
                        await output.ErrorAsync(text).ConfigureAwait(false);
                        break;
                    default:
                        await output.InfoAsync(text).ConfigureAwait(false);
                        break;
                }
            }

            return;
        }

        if (result.Status != CommandExecutionStatus.Success && !string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            await output.ErrorAsync(result.ErrorCode).ConfigureAwait(false);
        }
    }

    private async Task NotifyMutedAsync(PlayerId playerId, MuteSnapshot mute)
    {
        try
        {
            _ = await messenger.SendMessageAsync(
                playerId,
                $"You are muted until {mute.ExpiresAt:u}. Reason: {mute.Reason}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.Log(OperationsLogLevel.Warning, "moderation", "Unable to notify muted player.", ex);
        }
    }

    private void OnSessionClosed(PlayerSessionClosed closed) =>
        cultures.RemoveSession(CultureSubject(closed.PlayerId));

    private static string CultureSubject(PlayerId playerId) => $"player:{playerId.Slot}";

    private sealed class PlayerCommandPrincipal : IPlayerCommandPrincipal
    {
        private readonly IPlayerAuthorizationService authorization;

        public PlayerCommandPrincipal(
            string playerName,
            PlayerId playerId,
            IPlayerAuthorizationService authorization,
            AccountSnapshot? account)
        {
            PlayerName = playerName;
            PlayerId = playerId;
            this.authorization = authorization;
            Name = account?.Name ?? playerName;
            AccountName = account?.Name;
            CultureSubjectId = CultureSubject(playerId);
        }

        public string Name { get; }

        public bool IsServer => false;

        public string CultureSubjectId { get; }

        public PlayerId PlayerId { get; }

        public string PlayerName { get; }

        public string? AccountName { get; }

        public bool HasPermission(string permission) =>
            authorization.HasPermission(PlayerId, permission);
    }

    private sealed class PlayerCommandOutput(
        IPlayerMessenger messenger,
        PlayerId playerId) : ICommandOutput
    {
        public ValueTask InfoAsync(
            string message,
            CancellationToken cancellationToken = default) =>
            SendAsync(message, cancellationToken);

        public ValueTask SuccessAsync(
            string message,
            CancellationToken cancellationToken = default) =>
            SendAsync(message, cancellationToken);

        public ValueTask WarningAsync(
            string message,
            CancellationToken cancellationToken = default) =>
            SendAsync(message, cancellationToken);

        public ValueTask ErrorAsync(
            string message,
            CancellationToken cancellationToken = default) =>
            SendAsync(message, cancellationToken);

        private async ValueTask SendAsync(
            string message,
            CancellationToken cancellationToken)
        {
            _ = await messenger
                .SendMessageAsync(playerId, message, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
