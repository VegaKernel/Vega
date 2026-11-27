using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Commands;

/// <summary>Metadata for a command that passed dispatch checks and returned a result; never includes arguments or output.</summary>
public readonly record struct CommandCompletedObservation(WorldId WorldId, string CommandName,
    PlayerId? PlayerId, PlayerSessionId? SessionId, bool IsServer, CommandExecutionStatus Status);

public interface ICommandCompletionObservations
{
    event Action<CommandCompletedObservation>? Completed;
}
