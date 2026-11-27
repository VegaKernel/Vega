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
    public CommandDescriptor Descriptor { get; } = new("accountinfo", ["ai", "ai-id", "accountinfo-id"], ["vega.accounts.inspect", "vega.accounts.manage", "tshock.accountinfo.check"], Syntax: "accountinfo <account> | ai-id <account id>");
    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        bool byId = invocation.Name.EndsWith("-id", StringComparison.OrdinalIgnoreCase);
        if (invocation.Arguments.Count == 0 || (byId && (invocation.Arguments.Count != 1 || !int.TryParse(invocation.Arguments[0], out _))))
            return new(CommandExecutionStatus.InvalidUsage, "command.accountinfo.usage");
        string name = string.Join(" ", invocation.Arguments);
        var resolved = await (byId ? account.ResolveAccountAsync(name, token) : account.FindAccountByNameAsync(name, token)).ConfigureAwait(false);
        if (resolved is null) return CommandExecutionResult.Failed("command.account.not-found", MessageDescriptor.Error("command.account.not-found", name).WithDefaultText(byId ? "User with account id {0} does not exist." : "User {0} does not exist."));
        var messages = new List<MessageDescriptor>();
        if (resolved.LastSeenUtc is { } lastSeen)
            messages.Add(MessageDescriptor.Success("command.ai.last-login", resolved.Name, lastSeen.ToLocalTime().ToString("g zzz")).WithDefaultText("{0}'s last login occurred {1}."));
        else messages.Add(MessageDescriptor.Info("command.ai.never", resolved.Name).WithDefaultText("{0} has never logged in."));
        if (invocation.Principal.HasPermission("vega.accounts.inspect") || invocation.Principal.HasPermission("vega.accounts.manage") || invocation.Principal.HasPermission("tshock.accountinfo.details"))
        {
            messages.Add(MessageDescriptor.Success("command.ai.id", resolved.Name, resolved.Id.Value).WithDefaultText("{0}'s ID is {1}."));
            messages.Add(MessageDescriptor.Success("command.ai.group", resolved.Name, resolved.GroupName).WithDefaultText("{0}'s group is {1}."));
            if (invocation.Principal.HasPermission("vega.players.sensitive") || invocation.Principal.HasPermission("tshock.accountinfo.details.ip"))
                messages.Add(MessageDescriptor.Success("command.ai.ip", resolved.Name, resolved.LastKnownIp ?? "N/A").WithDefaultText("{0}'s last known IP is {1}."));
            if (invocation.Principal.HasPermission("vega.players.sensitive") || invocation.Principal.HasPermission("tshock.accountinfo.details.uuid"))
                messages.Add(MessageDescriptor.Success("command.ai.uuid", resolved.Name, UserInfoCommand.ShortUuid(resolved.LastKnownUuid)).WithDefaultText("{0}'s Short UUID is {1}."));
            if (resolved.RegisteredUtc is { } registered)
                messages.Add(MessageDescriptor.Success("command.ai.registered", resolved.Name, registered.ToLocalTime().ToString("g zzz")).WithDefaultText("{0} registered on {1}."));
        }
        return CommandExecutionResult.WithMessages([.. messages]);
    }
}

/// <summary>Temporary groups are durable bounded account leases, not an untracked slot override.</summary>
public sealed class TempGroupCommand(AccountCommand account) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("tempgroup", ["tempg", "tg"], ["vega.accounts.manage", "tshock.admin.tempgroup"],
        Syntax: "tempgroup add <account name/id> <group> [duration] | tempgroup del <account> | tempgroup info <account> | tempgroup list [page] | tempgroup help");
    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        if (args.Count == 1 && args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            return ValueTask.FromResult(CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.tempgroup.help", Descriptor.Syntax).WithDefaultText("{0}")));
        IReadOnlyList<string>? forwarded = invocation.Arguments.Count switch
        {
            1 or 2 when args[0].Equals("list", StringComparison.OrdinalIgnoreCase) => ["temp-list", .. args.Skip(1)],
            2 when args[0].Equals("info", StringComparison.OrdinalIgnoreCase) => ["temp-info", args[1]],
            2 when args[0].Equals("del", StringComparison.OrdinalIgnoreCase) => ["temp-clear", args[1]],
            3 or 4 when args[0].Equals("add", StringComparison.OrdinalIgnoreCase) => ["temp", .. args.Skip(1)],
            2 when invocation.Arguments[1].Equals("clear", StringComparison.OrdinalIgnoreCase) => ["temp-clear", invocation.Arguments[0]],
            3 => ["temp", .. invocation.Arguments],
            _ => null
        };
        return forwarded is not null ? account.ExecuteAsync(invocation with { Arguments = forwarded }, token)
            : ValueTask.FromResult(new CommandExecutionResult(CommandExecutionStatus.InvalidUsage, "command.tempgroup.usage"));
    }
}

public sealed class TempStatusCommand(AccountCommand account) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("tempstatus", ["temps", "ts"], ["tshock.admin.tempgroup.status", "vega.accounts.manage"], AllowServer: false, Syntax: "tempstatus");
    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token) =>
        invocation.Arguments.Count == 0 && invocation.Principal is IPlayerCommandPrincipal { AccountName: { } name }
            ? account.TemporaryGroupInfoAsync(invocation with { Arguments = ["temp-info", name] }, token)
            : ValueTask.FromResult(CommandExecutionResult.Failed("command.tempstatus.login", MessageDescriptor.Error("command.tempstatus.login").WithDefaultText("You're not logged in.")));
}
