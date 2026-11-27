namespace Vega.Contracts.Authorization;

public interface IAuthorizationService
{
    bool HasPermission(string groupName, string permission);

    bool TryGetGroup(string groupName, out GroupSnapshot? group);

    IReadOnlyList<GroupSnapshot> SnapshotGroups();

    ValueTask<GroupMutationResult> CreateGroupAsync(
        string name,
        string? parentName,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<GroupMutationResult> DeleteGroupAsync(
        string name,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<GroupMutationResult> SetParentAsync(
        string name,
        string? parentName,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<GroupMutationResult> SetPermissionAsync(
        string name,
        string permission,
        PermissionEffect? effect,
        long expectedRevision,
        string actor,
        CancellationToken cancellationToken = default);

    ValueTask<GroupMutationResult> SetPresentationAsync(
        string name, GroupChatPresentation presentation, long expectedRevision, string actor,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new GroupMutationResult(false, "group.presentation-unsupported"));
}

public enum PermissionEffect
{
    Deny = -1,
    Grant = 1
}

public sealed record GroupMutationResult(
    bool Succeeded,
    string Code,
    GroupSnapshot? Before = null,
    GroupSnapshot? After = null);
