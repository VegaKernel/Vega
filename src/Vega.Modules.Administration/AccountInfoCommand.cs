using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Modules.Administration;

/// <summary>
/// Read-only account inspection. Identity evidence is intentionally gated separately from
/// ordinary account metadata because IP addresses and client UUIDs are sensitive administrative data.
/// </summary>
public sealed class AccountInfoCommand(
    IAccountService accounts,
    IAccountIdentityEvidenceService identities) : ICommandHandler
{
    public const string Permission = "vega.accounts.inspect";
    public const string IdentityPermission = "vega.accounts.inspect.identity";

    public CommandDescriptor Descriptor { get; } = new(
        "accountinfo",
        ["ai"],
        [Permission],
        Syntax: "accountinfo <account-name>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 0)
        {
            return Usage();
        }

        string accountName = string.Join(' ', invocation.Arguments).Trim();
        if (accountName.Length == 0)
        {
            return Usage();
        }

        AccountSnapshot? account = await accounts.FindByNameAsync(accountName, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
        {
            return CommandExecutionResult.Failed(
                "command.accountinfo.not-found",
                MessageDescriptor.Error("command.accountinfo.not-found", accountName)
                    .WithDefaultText("Account '{0}' was not found."));
        }

        var messages = new List<MessageDescriptor>
        {
            MessageDescriptor.Info(
                    "command.accountinfo.summary",
                    account.Name,
                    account.Id.Value,
                    account.GroupName,
                    account.Culture ?? "default")
                .WithDefaultText("{0} | id {1} | group {2} | culture {3}"),
            MessageDescriptor.Info(
                    "command.accountinfo.last-login",
                    account.LastSeenUtc?.ToString("u") ?? "never")
                .WithDefaultText("Last login: {0}.")
        };

        if (invocation.Principal.IsServer || invocation.Principal.HasPermission(IdentityPermission))
        {
            IReadOnlyList<AccountIdentityEvidenceSnapshot> evidence = identities.SnapshotEvidence();
            AccountIdentityEvidenceSnapshot? latestIp = Latest(
                evidence,
                account.Id,
                AccountIdentityEvidenceKind.IpAddress);
            AccountIdentityEvidenceSnapshot? latestUuid = Latest(
                evidence,
                account.Id,
                AccountIdentityEvidenceKind.ClientUuid);

            messages.Add(
                MessageDescriptor.Info(
                        "command.accountinfo.identity",
                        latestIp?.Value ?? "none",
                        latestUuid?.Value ?? "none")
                    .WithDefaultText("Last known IP: {0}; last client UUID: {1}."));
        }

        return CommandExecutionResult.WithMessages(messages.ToArray());
    }

    private static AccountIdentityEvidenceSnapshot? Latest(
        IReadOnlyList<AccountIdentityEvidenceSnapshot> evidence,
        AccountId accountId,
        AccountIdentityEvidenceKind kind) => evidence
        .Where(item => item.AccountId == accountId && item.Kind == kind)
        .OrderByDescending(static item => item.LastSeenAt)
        .FirstOrDefault();

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.accountinfo.usage",
        [MessageDescriptor.Error("command.accountinfo.usage")
            .WithDefaultText("Usage: accountinfo <account-name>.")]);
}
