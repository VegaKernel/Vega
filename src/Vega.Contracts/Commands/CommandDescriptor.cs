using Vega.Contracts.Messaging;

namespace Vega.Contracts.Commands;

public sealed record CommandDescriptor(
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Permissions,
    bool AllowServer = true,
    string Syntax = "")
{
    /// <summary>Stable owning module/subsystem identity used by help and administration clients.</summary>
    public string Owner { get; init; } = "vega";

    /// <summary>Whether the command may be invoked by a non-server/player principal.</summary>
    public bool AllowPlayers { get; init; } = true;

    public string? TitleKey { get; init; }

    public string? DescriptionKey { get; init; }

    public string? HelpKey { get; init; }

    public string? FallbackTitle { get; init; }

    public string? FallbackDescription { get; init; }

    public string? FallbackHelp { get; init; }

    public bool IsAvailableTo(ICommandPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        bool transportAllowed = principal.IsServer ? AllowServer : AllowPlayers;
        return transportAllowed &&
            (Permissions.Count == 0 || Permissions.Any(principal.HasPermission));
    }
}

public enum CommandExecutionStatus
{
    Success,
    InvalidUsage,
    Forbidden,
    Failed
}

public readonly record struct CommandExecutionResult(
    CommandExecutionStatus Status,
    string? ErrorCode = null,
    IReadOnlyList<MessageDescriptor>? Messages = null)
{
    public static CommandExecutionResult Success => new(CommandExecutionStatus.Success);

    public static CommandExecutionResult WithMessages(params MessageDescriptor[] messages) =>
        new(CommandExecutionStatus.Success, Messages: messages);

    public static CommandExecutionResult Failed(
        string errorCode,
        params MessageDescriptor[] messages) =>
        new(CommandExecutionStatus.Failed, errorCode, messages);
}
