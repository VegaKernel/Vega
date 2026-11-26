using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Administration;

public sealed class ReferenceDataCommand(IWorldReferenceDataExporter exporter) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("dump-reference-data", [], ["vega.world.reference"], Syntax: "dump-reference-data [item|npc|buff|projectile|tile|all]");
    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        WorldReferenceKind kind = WorldReferenceKind.All;
        if (invocation.Arguments.Count > 1 || invocation.Arguments.Count == 1 && (!Enum.TryParse(invocation.Arguments[0], true, out kind) || !Enum.IsDefined(kind)))
            return new(CommandExecutionStatus.InvalidUsage, "reference.usage");
        var result = await exporter.ExportAsync(invocation.WorldId, kind, token).ConfigureAwait(false);
        return result.Succeeded ? CommandExecutionResult.WithMessages(MessageDescriptor.Success("reference.exported", result.EntryCount, result.RelativePath)
            .WithDefaultText("Exported {0} IDs to {1}.")) : CommandExecutionResult.Failed(result.ErrorCode ?? "reference.failed");
    }
}
