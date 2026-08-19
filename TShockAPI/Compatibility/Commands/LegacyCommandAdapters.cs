using TShockNG.Contracts.Commands;

namespace TShockAPI.Compatibility.Commands;

/// <summary>
/// Adapts the legacy <see cref="TSPlayer"/> identity to the modern command principal contract.
/// This type intentionally contains no command/domain logic.
/// </summary>
internal sealed class LegacyCommandPrincipal(TSPlayer player) : ICommandPrincipal
{
    public string Name => player.Name;

    public bool IsServer => !player.RealPlayer;

    public bool HasPermission(string permission) => player.HasPermission(permission);
}

/// <summary>
/// Compatibility-only output adapter for code that has not yet moved to structured command results.
/// New Core handlers should return MessageDescriptor values instead of calling this interface.
/// </summary>
internal sealed class LegacyCommandOutput(TSPlayer player) : ICommandOutput
{
    public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        player.SendInfoMessage(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        player.SendSuccessMessage(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        player.SendWarningMessage(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        player.SendErrorMessage(message);
        return ValueTask.CompletedTask;
    }
}
