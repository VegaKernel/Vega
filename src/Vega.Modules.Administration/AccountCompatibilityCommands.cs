using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Modules.Administration;

/// <summary>Vega bootstraps owners via the trusted console rather than an in-game shared setup token.</summary>
public sealed class SetupCommand : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("setup", [], ["vega.accounts.manage"], Syntax: "setup (server console)");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (!invocation.Principal.IsServer)
            return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.Forbidden, "setup.console_only"));
        if (invocation.Arguments.Count != 0)
            return ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "setup.usage"));
        return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Info("setup.console")
            .WithDefaultText("Create an administrator with: account create <name> <password> administrator. Then sign in as that account in-game. Vega does not issue remote setup tokens.")));
    }
}

/// <summary>Read-only account inspection uses the same account service and output as account show.</summary>
public sealed class AccountInfoCommand(AccountCommand account) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("accountinfo", ["ai"], ["vega.accounts.inspect", "vega.accounts.manage"], Syntax: "accountinfo <account>");
    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token) =>
        invocation.Arguments.Count == 1
            ? account.ExecuteAsync(invocation with { Arguments = ["show", invocation.Arguments[0]] }, token)
            : ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "command.accountinfo.usage"));
}

/// <summary>Temporary groups are durable bounded account leases, not an untracked slot override.</summary>
public sealed class TempGroupCommand(AccountCommand account) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("tempgroup", [], ["vega.accounts.manage"],
        Syntax: "tempgroup <account> <group> <duration> | tempgroup <account> clear | tempgroup list");
    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        IReadOnlyList<string>? forwarded = invocation.Arguments.Count switch
        {
            1 when invocation.Arguments[0].Equals("list", StringComparison.OrdinalIgnoreCase) => ["temp-list"],
            2 when invocation.Arguments[1].Equals("clear", StringComparison.OrdinalIgnoreCase) => ["temp-clear", invocation.Arguments[0]],
            3 => ["temp", .. invocation.Arguments],
            _ => null
        };
        return forwarded is not null ? account.ExecuteAsync(invocation with { Arguments = forwarded }, token)
            : ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "command.tempgroup.usage"));
    }
}
