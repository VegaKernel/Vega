namespace Vega.Contracts.Commands;

/// <summary>
/// Controls how a command invocation may be represented in administrative audit output.
/// </summary>
public enum CommandAuditMode
{
    /// <summary>
    /// Record only the canonical command name. This is the safe default for commands
    /// whose arguments may contain credentials, tokens or other sensitive values.
    /// </summary>
    NameOnly,

    /// <summary>
    /// Record the canonical command name and arguments. Use only when the command's
    /// argument contract is known not to contain secrets.
    /// </summary>
    NameAndArguments,

    /// <summary>
    /// Do not emit a normal command-execution audit record for this command.
    /// </summary>
    None
}
