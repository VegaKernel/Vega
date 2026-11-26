using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Localization;
using Vega.Contracts.Worlds;

namespace Vega.Operations.Actions;

public sealed class OperationsCommandService(
    ICommandDispatcher dispatcher,
    ILocalizationService localization,
    IPlayerCultureService cultures,
    WorldId? worldId = null) : IOperationsCommandService
{
    public async ValueTask<OperationsActionResult> ExecuteAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        if (!CommandTextParser.TryParse(commandText, out ParsedCommandText command))
        {
            return new OperationsActionResult(
                CommandExecutionStatus.InvalidUsage,
                "command.empty",
                ["Enter a command."]);
        }

        var output = new BufferedCommandOutput();
        var invocation = new CommandInvocation(
            command.Name,
            command.Arguments,
            OperationsPrincipal.Instance,
            output,
            command.Silent) { WorldId = worldId ?? WorldId.Primary };

        CommandExecutionResult result = await dispatcher
            .DispatchAsync(invocation, cancellationToken)
            .ConfigureAwait(false);

        var messages = output.Messages.ToList();
        if (result.Messages is not null)
        {
            messages.AddRange(result.Messages.Select(message =>
                localization.Format(message, cultures.ServerCulture)));
        }

        return new OperationsActionResult(
            result.Status,
            result.ErrorCode,
            Array.AsReadOnly(messages.ToArray()));
    }

    private sealed class OperationsPrincipal : ICommandPrincipal
    {
        public static OperationsPrincipal Instance { get; } = new();

        public string Name => "Vega Operations";

        public bool IsServer => true;

        public bool HasPermission(string permission) => true;
    }

    private sealed class BufferedCommandOutput : ICommandOutput
    {
        private readonly List<string> messages = [];

        public IReadOnlyList<string> Messages => messages;

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => Add(message);

        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => Add(message);

        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => Add(message);

        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => Add(message);

        private ValueTask Add(string message)
        {
            messages.Add(message);
            return ValueTask.CompletedTask;
        }
    }
}
