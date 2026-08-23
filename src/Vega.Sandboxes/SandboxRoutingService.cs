using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxRoutingService(
    ISandboxDirectory sandboxes,
    ISandboxTransferTicketService tickets,
    SandboxEndpoint primaryEndpoint,
    ISandboxSscStore? sscStore = null) : ISandboxRoutingService
{
    public SandboxTransferResult PrepareEnter(
        string accountId,
        IReadOnlySet<string> permissions,
        WorldId originWorldId,
        WorldId targetWorldId) =>
        PrepareTransfer(accountId, permissions, originWorldId, targetWorldId);

    public SandboxTransferResult PrepareLeave(string accountId, WorldId originWorldId)
    {
        SandboxSscMode originMode = ModeFor(originWorldId);
        SandboxSscMode targetMode = sscStore is null ? SandboxSscMode.Disabled : SandboxSscMode.Inherit;
        SandboxSscTransferPreparation prepared = PrepareSsc(
            accountId, originWorldId, WorldId.Primary, originMode, targetMode);
        if (!prepared.Accepted)
        {
            return Reject(originWorldId, prepared.ErrorCode ?? "sandbox.ssc.rejected",
                prepared.Message ?? "SSC transfer preparation failed.");
        }

        SandboxTransferTicket ticket = tickets.Issue(
            accountId, originWorldId, WorldId.Primary, prepared.Lease?.LeaseId);
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

        SandboxSscMode originMode = ModeFor(originWorldId);
        SandboxSscTransferPreparation ssc = PrepareSsc(
            accountId, originWorldId, targetWorldId, originMode, target.Descriptor.SscMode);
        if (!ssc.Accepted)
        {
            return Reject(originWorldId, ssc.ErrorCode ?? "sandbox.ssc.rejected",
                ssc.Message ?? "SSC transfer preparation failed.");
        }

        SandboxTransferTicket ticket = tickets.Issue(
            accountId, originWorldId, targetWorldId, ssc.Lease?.LeaseId);
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
        if (validation != SandboxTransferValidationStatus.Accepted)
        {
            return Reject(ticket.OriginWorldId, $"sandbox.transfer.{validation.ToString().ToLowerInvariant()}",
                $"Transfer ticket was rejected: {validation}.");
        }

        if (ticket.SscLeaseId is Guid leaseId && sscStore is not null)
        {
            SandboxSscTransferPreparation committed = sscStore.CommitTransfer(
                leaseId, ticket.AccountId, receivingWorldId, DateTimeOffset.UtcNow);
            if (!committed.Accepted)
            {
                return Reject(ticket.OriginWorldId, committed.ErrorCode ?? "sandbox.ssc.commit_failed",
                    committed.Message ?? "SSC transfer commit failed.");
            }
        }

        return new SandboxTransferResult(true, receivingWorldId, null, ticket);
    }

    private SandboxSscTransferPreparation PrepareSsc(
        string accountId,
        WorldId originWorldId,
        WorldId targetWorldId,
        SandboxSscMode originMode,
        SandboxSscMode targetMode)
    {
        if (sscStore is null)
        {
            return originMode == SandboxSscMode.Disabled && targetMode == SandboxSscMode.Disabled
                ? new SandboxSscTransferPreparation(true)
                : new SandboxSscTransferPreparation(
                    false,
                    ErrorCode: "sandbox.ssc.store_unavailable",
                    Message: "SSC persistence is unavailable.");
        }

        return sscStore.PrepareTransfer(
            accountId, originWorldId, targetWorldId, originMode, targetMode,
            DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    private SandboxSscMode ModeFor(WorldId worldId)
    {
        if (worldId == WorldId.Primary)
        {
            return sscStore is null ? SandboxSscMode.Disabled : SandboxSscMode.Inherit;
        }

        return sandboxes.Snapshot().FirstOrDefault(status => status.Descriptor.WorldId == worldId)?.Descriptor.SscMode
            ?? SandboxSscMode.Disabled;
    }

    private static SandboxTransferResult Reject(WorldId fallback, string code, string message) =>
        new(false, fallback, null, null, code, message);
}
