using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxPlayerTransferCommand(
    ISandboxDirectory sandboxes,
    ISandboxRoutingService routing,
    ISandboxPlayerTransferBoundary boundary,
    IPlayerDirectory players,
    IPlayerAuthorizationService authorization,
    IPlayerAccountSessionService accounts) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "worldtransfer",
        ["sendworld"],
        ["vega.sandboxes.transfer"],
        Syntax: "worldtransfer <player> <target-world>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count != 2 ||
            !players.TryFindExact(invocation.Arguments[0], out PlayerSnapshot? player) || player is null)
        {
            return Failure("sandbox.transfer.player_not_found", "Player was not found; use an exact online name.");
        }

        var targetId = new WorldId(invocation.Arguments[1]);
        SandboxStatus? target = sandboxes.Snapshot().FirstOrDefault(status => status.Descriptor.WorldId == targetId);
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (target?.Descriptor.Access.Permission is string permission &&
            authorization.HasPermission(player.Id, permission))
        {
            permissions.Add(permission);
        }

        string accountId = accounts.TryGetAccount(player.Id, out AccountSnapshot? account) && account is not null
            ? account.Id.Value.ToString()
            : $"guest:{player.Name}";
        SandboxTransferResult prepared = routing.PrepareTransfer(
            accountId,
            permissions,
            player.WorldId,
            targetId);
        if (!prepared.Accepted || prepared.Endpoint is null || prepared.Ticket is null)
        {
            return Failure(prepared.ErrorCode ?? "sandbox.transfer.rejected", prepared.Message ?? "Transfer rejected.");
        }

        SandboxPlayerTransferResult applied = await boundary.TransferAsync(
            player,
            prepared.Endpoint,
            prepared.Ticket,
            cancellationToken).ConfigureAwait(false);
        return applied.Applied
            ? CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.sandbox.transfer", player.Name, targetId.Value)
                    .WithDefaultText("Transferring {0} to {1}."))
            : Failure(applied.ErrorCode ?? "sandbox.transfer.failed", "The client could not be transferred.");
    }

    private static CommandExecutionResult Failure(string code, string text) => CommandExecutionResult.Failed(
        code,
        MessageDescriptor.Error(code, text).WithDefaultText("{0}"));
}
