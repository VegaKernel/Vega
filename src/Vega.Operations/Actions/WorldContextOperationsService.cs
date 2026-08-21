using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Operations.Actions;

public sealed class WorldContextOperationsService(IWorldContextOrchestrator orchestrator) : IOperationsWorldService
{
    public async ValueTask<OperationsCommandResult> MovePlayerAsync(
        PlayerId playerId,
        WorldId targetWorldId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await orchestrator.MovePlayerAsync(playerId, targetWorldId, cancellationToken).ConfigureAwait(false);
            return new OperationsCommandResult(
                OperationsCommandExitCode.Success,
                $"Moved player {playerId} to world '{targetWorldId}'.");
        }
        catch (ArgumentException ex)
        {
            return new OperationsCommandResult(
                OperationsCommandExitCode.ValidationError,
                ex.Message,
                "operations.world.move.invalid");
        }
        catch (InvalidOperationException ex)
        {
            return new OperationsCommandResult(
                OperationsCommandExitCode.Failed,
                ex.Message,
                "operations.world.move.failed");
        }
    }
}
