namespace Vega.Contracts.Commands;

/// <summary>
/// Read-only diagnostic view of a currently registered command and the code that owns its handler.
/// This is intentionally separate from <see cref="CommandDescriptor"/> so execution metadata stays small.
/// </summary>
public sealed record CommandRegistrationSnapshot(
    CommandDescriptor Descriptor,
    string OwnerAssembly,
    string HandlerType);

/// <summary>
/// Diagnostic catalog of live command registrations, including hot-reloaded plugin handlers.
/// </summary>
public interface ICommandRegistrationCatalog
{
    IReadOnlyList<CommandRegistrationSnapshot> SnapshotRegistrations();
}
