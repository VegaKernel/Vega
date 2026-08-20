using Multiplicity.Packets;
using Multiplicity.Packets.Views;
using OTAPI;
using TShockNG.Contracts.Authorization;
using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Messaging;
using TShockNG.Contracts.Players;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Routes Vega-owned player chat commands through the modern command dispatcher before legacy TSAPI sees them.
/// Non-command chat and unknown commands continue through the legacy path while migration is incomplete.
/// </summary>
internal sealed class NativePlayerCommandHooks : IDisposable
{
    private readonly ICommandDispatcher dispatcher;
    private readonly IPlayerAuthorizationService authorization;
    private readonly IPlayerDirectory players;
    private readonly IPlayerMessenger messenger;
    private bool attached;

    public NativePlayerCommandHooks(
        ICommandDispatcher dispatcher,
        IPlayerAuthorizationService authorization,
        IPlayerDirectory players,
        IPlayerMessenger messenger)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.messenger = messenger ?? throw new ArgumentNullException(nameof(messenger));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData += OnReceiveData;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.MessageBuffer.GetData -= OnReceiveData;
        attached = false;
    }

    private void OnReceiveData(object? sender, Hooks.MessageBuffer.GetDataEventArgs args)
    {
        if (args.Result == HookResult.Cancel || args.PacketId != (byte)PacketTypes.LoadNetModule)
        {
            return;
        }

        if (!TryGetPlayerCommand(args, out string commandText) ||
            commandText.Length < 2 ||
            (commandText[0] != '/' && commandText[0] != '.'))
        {
            return;
        }

        if (!CommandTextParser.TryParse(commandText, out ParsedCommandText command) ||
            !dispatcher.CanDispatch(command.Name))
        {
            return;
        }

        var playerId = new PlayerId(args.Instance.whoAmI);
        if (!players.TryGet(playerId, out PlayerSnapshot? player) || player is null)
        {
            return;
        }

        // This synchronous OTAPI hook only claims commands already owned by Vega.
        // Execution and game-thread message delivery continue asynchronously without blocking packet processing.
        args.Result = HookResult.Cancel;
        _ = ExecuteAsync(playerId, player.Name, command);
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
            // Malformed or currently unsupported chat payloads stay on the legacy path during migration.
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
            var invocation = new CommandInvocation(
                command.Name,
                command.Arguments,
                new PlayerCommandPrincipal(playerName, playerId, authorization),
                output,
                command.Silent);

            CommandExecutionResult result = await dispatcher
                .DispatchAsync(invocation)
                .ConfigureAwait(false);

            await WriteResultAsync(output, result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Vega player command '{command.Name}' failed for slot {playerId.Value}: {ex}");
        }
    }

    private static async ValueTask WriteResultAsync(
        ICommandOutput output,
        CommandExecutionResult result)
    {
        if (result.Messages is { Count: > 0 })
        {
            foreach (MessageDescriptor message in result.Messages)
            {
                string text = MessageFallbackFormatter.Format(message);
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

    private sealed class PlayerCommandPrincipal(
        string name,
        PlayerId playerId,
        IPlayerAuthorizationService authorization) : ICommandPrincipal
    {
        public string Name { get; } = name;

        public bool IsServer => false;

        public bool HasPermission(string permission) =>
            authorization.HasPermission(playerId, permission);
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
