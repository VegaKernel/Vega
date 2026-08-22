using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Chests;

public sealed class ChestPolicyCommand(ChestPolicyModule module) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "chestpolicy",
        ["infchest", "chestregen"],
        ["vega.chests.manage"],
        Syntax: "chestpolicy <list|capture|restore|remove> ...");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 1 &&
            string.Equals(invocation.Arguments[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            ChestTemplate[] templates = module.SnapshotTemplates().ToArray();
            return templates.Length == 0
                ? Info("No chest templates configured.")
                : CommandExecutionResult.WithMessages(templates.Select(template =>
                    MessageDescriptor.Info("command.chestpolicy.entry").WithDefaultText(
                        $"{template.Identity} mode={template.Mode} delay={template.DelaySeconds}s items={template.Items.Count}."))
                    .ToArray());
        }

        if (invocation.Arguments.Count >= 3 &&
            int.TryParse(invocation.Arguments[^2], out int x) &&
            int.TryParse(invocation.Arguments[^1], out int y))
        {
            var identity = new ChestIdentity(invocation.WorldId, x, y);
            string action = invocation.Arguments[0].ToLowerInvariant();
            if (action == "restore" && invocation.Arguments.Count == 3)
            {
                return await module.RestoreNowAsync(identity, cancellationToken).ConfigureAwait(false)
                    ? Success("Chest restored from its template.")
                    : Failure("chest.restore_failed", "Chest template could not be restored.");
            }

            if (action == "remove" && invocation.Arguments.Count == 3)
            {
                return module.RemoveTemplate(identity)
                    ? Success("Chest template removed.")
                    : Failure("chest.template_not_found", "Chest template not found.");
            }
        }

        if (invocation.Arguments.Count is 4 or 5 &&
            string.Equals(invocation.Arguments[0], "capture", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse(invocation.Arguments[1], true, out ChestTemplateMode mode) &&
            int.TryParse(invocation.Arguments[2], out int captureX) &&
            int.TryParse(invocation.Arguments[3], out int captureY) &&
            (invocation.Arguments.Count == 4 || int.TryParse(invocation.Arguments[4], out _)))
        {
            int delay = invocation.Arguments.Count == 5 ? int.Parse(invocation.Arguments[4]) : 0;
            bool captured = await module.CaptureTemplateAsync(
                new ChestIdentity(invocation.WorldId, captureX, captureY),
                mode,
                delay,
                cancellationToken).ConfigureAwait(false);
            return captured
                ? Success("Chest template captured.")
                : Failure("chest.capture_failed", "Chest template could not be captured.");
        }

        return Failure(
            "command.chestpolicy.usage",
            "Usage: chestpolicy list | capture <infinite|regenerate> <x> <y> [delay-seconds] | restore <x> <y> | remove <x> <y>");
    }

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(
        MessageDescriptor.Info("command.chestpolicy.info").WithDefaultText(text));

    private static CommandExecutionResult Success(string text) => CommandExecutionResult.WithMessages(
        MessageDescriptor.Success("command.chestpolicy.success").WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
