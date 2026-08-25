namespace Vega.Contracts.Commands;

/// <summary>
/// A command execution record whose arguments have already been filtered according to
/// <see cref="CommandDescriptor.AuditMode"/>. Audit sinks must never receive the original
/// invocation when the descriptor does not explicitly permit argument logging.
/// </summary>
public sealed record CommandAuditRecord(
    string CommandName,
    IReadOnlyList<string> Arguments,
    string PrincipalName,
    bool PrincipalIsServer,
    CommandExecutionStatus Status,
    string? ErrorCode);

public interface ICommandAuditSink
{
    void Record(CommandAuditRecord record);
}
