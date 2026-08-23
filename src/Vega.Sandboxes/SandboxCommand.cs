using System.Globalization;
using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxCommand(
    ISandboxControlService sandboxes,
    ISandboxRoutingService routing,
    IOperationsLogger logger) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "sandbox",
        ["worlds"],
        ["vega.sandboxes.manage"],
        Syntax: "sandbox list | status <id> | create <id> <name> <source.wld> <temporary|persistent|external> <capacity> [disabled|inherit|copyonenter|isolated] [port] | start|stop|restart <id> | reset <id> [source.wld] confirm | destroy <id> confirm | rolling-update confirm | transfer <account> <origin> <target>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0) return Usage();
        string action = invocation.Arguments[0].ToLowerInvariant();
        CommandExecutionResult result = action switch
        {
            "list" => List(),
            "status" => Status(invocation.Arguments),
            "create" => await CreateAsync(invocation.Arguments, cancellationToken).ConfigureAwait(false),
            "start" => await RunAsync(invocation.Arguments, sandboxes.StartAsync, cancellationToken).ConfigureAwait(false),
            "stop" => await RunAsync(invocation.Arguments, sandboxes.StopAsync, cancellationToken).ConfigureAwait(false),
            "restart" => await RunAsync(invocation.Arguments, sandboxes.RestartAsync, cancellationToken).ConfigureAwait(false),
            "reset" => await ResetAsync(invocation.Arguments, cancellationToken).ConfigureAwait(false),
            "destroy" => await DestroyAsync(invocation.Arguments, cancellationToken).ConfigureAwait(false),
            "rolling-update" => await RollingUpdateAsync(invocation.Arguments, cancellationToken).ConfigureAwait(false),
            "transfer" => Transfer(invocation.Arguments),
            _ => Usage()
        };
        logger.Log(
            result.Status == CommandExecutionStatus.Success ? OperationsLogLevel.Information : OperationsLogLevel.Warning,
            "sandbox-audit",
            $"actor={invocation.Principal.Name}; action={string.Join(' ', invocation.Arguments)}; outcome={result.Status}; error={result.ErrorCode ?? "none"}");
        return result;
    }

    private CommandExecutionResult List()
    {
        SandboxStatus[] snapshot = sandboxes.Snapshot().ToArray();
        return snapshot.Length == 0
            ? Success("No sandboxes are defined.")
            : Messages(snapshot.Select(Render).ToArray());
    }

    private CommandExecutionResult Status(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 2) return Usage();
        SandboxStatus? status = Find(arguments[1]);
        return status is null ? Failure("sandbox.not_found", $"Sandbox '{arguments[1]}' was not found.") : Success(Render(status));
    }

    private async ValueTask<CommandExecutionResult> CreateAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count is < 6 or > 8 ||
            !Enum.TryParse(arguments[4], true, out SandboxPersistenceMode persistence) ||
            !int.TryParse(arguments[5], NumberStyles.None, CultureInfo.InvariantCulture, out int capacity))
        {
            return Usage();
        }

        SandboxSscMode sscMode = SandboxSscMode.Disabled;
        int? port = null;
        if (arguments.Count >= 7)
        {
            if (Enum.TryParse(arguments[6], true, out SandboxSscMode parsedMode))
            {
                sscMode = parsedMode;
            }
            else if (arguments.Count == 7 &&
                     int.TryParse(arguments[6], NumberStyles.None, CultureInfo.InvariantCulture, out int legacyPort))
            {
                port = legacyPort;
            }
            else
            {
                return Usage();
            }
        }

        if (arguments.Count == 8 &&
            !int.TryParse(arguments[7], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort))
        {
            return Usage();
        }
        if (arguments.Count == 8) port = int.Parse(arguments[7], CultureInfo.InvariantCulture);

        var request = new SandboxCreateRequest(
            arguments[1],
            arguments[2],
            arguments[3],
            persistence,
            capacity,
            new SandboxAccessPolicy(SandboxAccessMode.Public),
            null,
            sscMode,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            port);
        return Result(await sandboxes.CreateAsync(request, cancellationToken).ConfigureAwait(false));
    }

    private static async ValueTask<CommandExecutionResult> RunAsync(
        IReadOnlyList<string> arguments,
        Func<WorldId, CancellationToken, ValueTask<SandboxOperationResult>> operation,
        CancellationToken cancellationToken)
    {
        return arguments.Count == 2
            ? Result(await operation(new WorldId(arguments[1]), cancellationToken).ConfigureAwait(false))
            : Usage();
    }

    private async ValueTask<CommandExecutionResult> ResetAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count is < 3 or > 4 || !string.Equals(arguments[^1], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("sandbox.confirmation_required", "Use: sandbox reset <id> [source.wld] confirm.");
        }

        string? source = arguments.Count == 4 ? arguments[2] : null;
        return Result(await sandboxes.ResetAsync(new WorldId(arguments[1]), source, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<CommandExecutionResult> DestroyAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 3 || !string.Equals(arguments[2], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("sandbox.confirmation_required", "Use: sandbox destroy <id> confirm.");
        }

        return Result(await sandboxes.DestroyAsync(new WorldId(arguments[1]), cancellationToken).ConfigureAwait(false));
    }

    private CommandExecutionResult Transfer(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4) return Usage();
        SandboxTransferResult result = routing.PrepareTransfer(
            arguments[1],
            new HashSet<string>(),
            new WorldId(arguments[2]),
            new WorldId(arguments[3]));
        return !result.Accepted || result.Ticket is null
            ? Failure(result.ErrorCode ?? "sandbox.transfer_failed", result.Message ?? "Transfer failed.")
            : Success(JsonSerializer.Serialize(new
            {
                target = result.EffectiveWorldId.Value,
                endpoint = result.Endpoint,
                ticket = result.Ticket
            }));
    }

    private async ValueTask<CommandExecutionResult> RollingUpdateAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Count != 2 || !string.Equals(arguments[1], "confirm", StringComparison.OrdinalIgnoreCase))
        {
            return Failure("sandbox.confirmation_required", "Use: sandbox rolling-update confirm.");
        }

        var messages = new List<string>();
        foreach (WorldId worldId in sandboxes.Snapshot()
                     .Where(static status => status.DesiredRunning)
                     .Select(static status => status.Descriptor.WorldId))
        {
            SandboxOperationResult result = await sandboxes.RestartAsync(worldId, cancellationToken).ConfigureAwait(false);
            messages.Add($"{worldId}: {result.Status} ({result.Message})");
            if (!result.IsSuccess) break;
        }

        return messages.Count == 0
            ? Success("No running sandbox requires an update.")
            : Messages(messages.ToArray());
    }

    private SandboxStatus? Find(string id) => sandboxes.Snapshot().FirstOrDefault(status =>
        string.Equals(status.Descriptor.WorldId.Value, id, StringComparison.OrdinalIgnoreCase));

    private static string Render(SandboxStatus status) =>
        $"{status.Descriptor.WorldId} | {status.State} | {status.OnlinePlayers}/{status.Descriptor.PlayerCapacity} | " +
        $"port {status.Descriptor.ListenerPort} | pid {status.ProcessId?.ToString() ?? "-"} | " +
        $"memory {status.Resources.WorkingSetBytes / 1024 / 1024} MiB | restarts {status.RestartCount}" +
        (status.Failure is null ? string.Empty : $" | {status.Failure}");

    private static CommandExecutionResult Result(SandboxOperationResult result) => result.IsSuccess
        ? Success(result.Message ?? "Sandbox operation completed.")
        : Failure(result.ErrorCode ?? "sandbox.operation_failed", result.Message ?? "Sandbox operation failed.");

    private static CommandExecutionResult Messages(params string[] values) => CommandExecutionResult.WithMessages(
        values.Select(value => MessageDescriptor.Info("command.sandbox.entry", value).WithDefaultText("{0}")).ToArray());

    private static CommandExecutionResult Success(string value) => CommandExecutionResult.WithMessages(
        MessageDescriptor.Success("command.sandbox.success", value).WithDefaultText("{0}"));

    private static CommandExecutionResult Failure(string code, string value) => CommandExecutionResult.Failed(
        code,
        MessageDescriptor.Error(code, value).WithDefaultText("{0}"));

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.sandbox.usage",
        [MessageDescriptor.Error("command.sandbox.usage").WithDefaultText(
            "Usage: sandbox list | status/create/start/stop/restart/reset/destroy/transfer ...")]);
}
