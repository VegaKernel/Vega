using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using TerrariaRuntime;
using Vega.Contracts.Authorization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Operations;
using Vega.Contracts.Moderation;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;

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
    private readonly IChatPipeline chat;
    private readonly Func<PlayerSnapshot, bool>? gameplayReadiness;
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
        IChatPipeline chat,
        Func<PlayerSnapshot, bool>? gameplayReadiness = null)
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
        this.chat = chat ?? throw new ArgumentNullException(nameof(chat));
        this.gameplayReadiness = gameplayReadiness;
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
            // This synchronous TerrariaRuntime hook only claims commands already owned by Vega.
            // Execution and game-thread message delivery continue asynchronously without blocking packet processing.
            args.Result = HookResult.Cancel;
            _ = ExecuteAsync(player, command);
            return;
        }

        var audience = players.SnapshotOnlinePlayers().Where(recipient => recipient.WorldId == player.WorldId).ToArray();
        var chatContext = new ChatMessageContext(
            player.WorldId,
            player,
            messageText,
            audience);
        ChatPipelineResult chatResult = chat.Process(chatContext);
        if (!chatResult.Allowed)
        {
            args.Result = HookResult.Cancel;
            _ = messenger.SendMessageAsync(player, chatResult.RejectionReason ?? "Chat message rejected.");
            logger.Log(
                OperationsLogLevel.Information,
                "moderation",
                $"Chat rejected for {player.Name}: {chatResult.RejectionReason}",
                correlationId: player.SessionId?.ToString());
            return;
        }

        if (chatResult.RequiresCustomDelivery)
        {
            args.Result = HookResult.Cancel;
            _ = DeliverChatAsync(chatResult.Deliveries, audience);
        }

        if (chat is IPublicChatObservationSink observationSink)
            observationSink.ObserveAccepted(chatContext, chatResult);

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

        if (!MultiplicityPacketProcessing.TryParseInboundPayload(
                args.PacketId,
                args.Instance.readBuffer,
                args.ReadOffset,
                args.GetPayloadLength(),
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
        PlayerSnapshot player,
        ParsedCommandText command)
    {
        PlayerId playerId = player.Id;
        try
        {
            if (!players.TryGet(playerId, out var current) || current is null || current.SessionId != player.SessionId || current.WorldId != player.WorldId) return;
            var output = new PlayerCommandOutput(messenger, player);
            if (gameplayReadiness is not null && !IsAllowedBeforeReadiness(command.Name) && !gameplayReadiness(current))
            {
                await output.WarningAsync("Please authenticate and wait for your character to be ready before using gameplay commands.").ConfigureAwait(false);
                return;
            }
            accountSessions.TryGetAccount(playerId, out AccountSnapshot? account);
            var invocation = new CommandInvocation(
                command.Name,
                command.Arguments,
                new PlayerCommandPrincipal(player, players, authorization, account),
                output,
                command.Silent)
            {
                WorldId = player.WorldId
            };

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

    private async Task DeliverChatAsync(IReadOnlyList<ChatDelivery> deliveries, IReadOnlyList<PlayerSnapshot> audience)
    {
        var recipients = audience.ToDictionary(player => player.Id);
        foreach (ChatDelivery delivery in deliveries)
        {
            try
            {
                if (!recipients.TryGetValue(delivery.RecipientId, out var recipient)) continue;
                _ = await messenger.SendChatMessageAsync(recipient, delivery.Content, delivery.Color)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.Log(OperationsLogLevel.Warning, "chat", "Unable to deliver transformed chat.", ex);
            }
        }
    }

    private void OnSessionClosed(PlayerSessionClosed closed) =>
        cultures.RemoveSession(CultureSubject(closed.PlayerId));

    private static string CultureSubject(PlayerId playerId) => $"player:{playerId.Slot}";

    private static readonly HashSet<string> CommandsBeforeReadiness = new(StringComparer.OrdinalIgnoreCase)
    {
        "login", "auth", "register", "logout", "password", "passwd", "lang", "language", "help", "commands", "aliases",
        "playing", "who", "online", "version", "rules", "motd", "whisper", "w", "tell", "pm", "dm", "reply", "r", "wallow", "wa", "party", "p", "me"
    };

    internal static bool IsAllowedBeforeReadiness(string command) => CommandsBeforeReadiness.Contains(command);

    private sealed class PlayerCommandPrincipal : IPlayerCommandPrincipal
    {
        private readonly IPlayerAuthorizationService authorization;
        private readonly IPlayerDirectory players;
        private readonly WorldId world;

        public PlayerCommandPrincipal(
            PlayerSnapshot player,
            IPlayerDirectory players,
            IPlayerAuthorizationService authorization,
            AccountSnapshot? account)
        {
            PlayerName = player.Name;
            PlayerId = player.Id;
            SessionId = player.SessionId;
            world = player.WorldId;
            this.players = players;
            this.authorization = authorization;
            Name = account?.Name ?? player.Name;
            AccountName = account?.Name;
            CultureSubjectId = CultureSubject(player.Id);
        }

        public string Name { get; }

        public bool IsServer => false;

        public string CultureSubjectId { get; }

        public PlayerId PlayerId { get; }

        public PlayerSessionId? SessionId { get; }

        public string PlayerName { get; }

        public string? AccountName { get; }

        public bool HasPermission(string permission) =>
            players.TryGet(PlayerId, out var current) && current is not null && current.SessionId == SessionId && current.WorldId == world &&
            authorization.HasPermission(PlayerId, permission);
    }

    private sealed class PlayerCommandOutput(
        IPlayerMessenger messenger,
        PlayerSnapshot player) : ICommandOutput
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
                .SendMessageAsync(player, message, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
