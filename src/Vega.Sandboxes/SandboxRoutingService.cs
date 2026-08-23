using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxRoutingService(
    ISandboxControlService sandboxes,
    ISandboxTransferTicketService tickets,
    SandboxEndpoint primaryEndpoint) : ISandboxRoutingService
{
    public SandboxTransferResult PrepareEnter(
        string accountId,
        IReadOnlySet<string> permissions,
        WorldId originWorldId,
        WorldId targetWorldId) =>
        PrepareTransfer(accountId, permissions, originWorldId, targetWorldId);

    public SandboxTransferResult PrepareLeave(string accountId, WorldId originWorldId)
    {
        SandboxTransferTicket ticket = tickets.Issue(accountId, originWorldId, WorldId.Primary);
        return new SandboxTransferResult(true, WorldId.Primary, primaryEndpoint, ticket);
    }

    public SandboxTransferResult PrepareTransfer(
        string accountId,
        IReadOnlySet<string> permissions,
        WorldId originWorldId,
        WorldId targetWorldId)
    {
        if (targetWorldId == WorldId.Primary)
        {
            return PrepareLeave(accountId, originWorldId);
        }

        SandboxStatus? target = sandboxes.Snapshot().FirstOrDefault(status =>
            status.Descriptor.WorldId == targetWorldId);
        if (target is null)
        {
            return Reject(originWorldId, "sandbox.transfer.not_found", "Target world was not found.");
        }

        if (target.State != SandboxLifecycleState.Ready || target.Endpoint is null)
        {
            return Reject(originWorldId, "sandbox.transfer.not_ready", "Target world is not ready.");
        }

        if (target.OnlinePlayers >= target.Descriptor.PlayerCapacity)
        {
            return Reject(originWorldId, "sandbox.transfer.full", "Target world is full.");
        }

        SandboxAccessPolicy access = target.Descriptor.Access;
        bool admitted = access.Mode switch
        {
            SandboxAccessMode.Public => true,
            SandboxAccessMode.Permission => access.Permission is not null && permissions.Contains(access.Permission),
            SandboxAccessMode.AllowList => access.AllowedAccounts?.Contains(accountId) == true,
            _ => false
        };
        if (!admitted)
        {
            return Reject(originWorldId, "sandbox.transfer.forbidden", "Target world access policy rejected the account.");
        }

        SandboxTransferTicket ticket = tickets.Issue(accountId, originWorldId, targetWorldId);
        return new SandboxTransferResult(true, targetWorldId, target.Endpoint, ticket);
    }

    public SandboxTransferResult AcceptTransfer(SandboxTransferTicket ticket, WorldId receivingWorldId)
    {
        if (receivingWorldId != WorldId.Primary && !sandboxes.Snapshot().Any(status =>
                status.Descriptor.WorldId == receivingWorldId && status.State == SandboxLifecycleState.Ready))
        {
            return Reject(ticket.OriginWorldId, "sandbox.transfer.backend_lost", "Target backend is no longer ready.");
        }

        SandboxTransferValidationStatus validation = tickets.ValidateAndConsume(
            ticket,
            receivingWorldId,
            DateTimeOffset.UtcNow);
        return validation == SandboxTransferValidationStatus.Accepted
            ? new SandboxTransferResult(true, receivingWorldId, null, ticket)
            : Reject(ticket.OriginWorldId, $"sandbox.transfer.{validation.ToString().ToLowerInvariant()}",
                $"Transfer ticket was rejected: {validation}.");
    }

    private static SandboxTransferResult Reject(WorldId fallback, string code, string message) =>
        new(false, fallback, null, null, code, message);
}
