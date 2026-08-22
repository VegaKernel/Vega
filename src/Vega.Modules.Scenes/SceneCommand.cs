using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;

namespace Vega.Modules.Scenes;

public sealed class SceneCommand(
    SceneConsumersModule module,
    ISceneRuntime scenes,
    IPlayerDirectory players) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "scene",
        ["regionvision"],
        ["vega.scenes.manage"],
        Syntax: "scene <list|border <player> <x> <y> <width> <height> [tile]|clear <player>>");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (invocation.Arguments.Count == 1 &&
            string.Equals(invocation.Arguments[0], "list", StringComparison.OrdinalIgnoreCase))
        {
            SceneSnapshot[] snapshots = scenes.SnapshotScenes().ToArray();
            return ValueTask.FromResult(snapshots.Length == 0
                ? Info("No active scenes.")
                : CommandExecutionResult.WithMessages(snapshots.Select(scene =>
                    MessageDescriptor.Info("command.scene.entry").WithDefaultText(
                        $"{scene.Name}: owner={scene.Owner}, overlays={scene.Overlays.Count}, conflicts={scene.ConflictCount}, dirty={scene.DirtyRegions.Count}."))
                    .ToArray()));
        }

        if (invocation.Arguments.Count == 2 &&
            string.Equals(invocation.Arguments[0], "clear", StringComparison.OrdinalIgnoreCase) &&
            players.TryFindExact(invocation.Arguments[1], out PlayerSnapshot? clearPlayer) && clearPlayer is not null)
        {
            bool cleared = module.RegionVision.Clear(clearPlayer.Id) | module.WorldEdit.Clear(clearPlayer.Id);
            return ValueTask.FromResult(cleared ? Info("Player scene overlays cleared.") : Info("No player overlays found."));
        }

        if (invocation.Arguments.Count is 6 or 7 &&
            string.Equals(invocation.Arguments[0], "border", StringComparison.OrdinalIgnoreCase) &&
            players.TryFindExact(invocation.Arguments[1], out PlayerSnapshot? target) && target is not null &&
            int.TryParse(invocation.Arguments[2], out int x) &&
            int.TryParse(invocation.Arguments[3], out int y) &&
            int.TryParse(invocation.Arguments[4], out int width) &&
            int.TryParse(invocation.Arguments[5], out int height) &&
            width is >= 2 and <= 255 && height is >= 2 and <= 255 &&
            (invocation.Arguments.Count == 6 || ushort.TryParse(invocation.Arguments[6], out _)))
        {
            ushort tile = invocation.Arguments.Count == 7 ? ushort.Parse(invocation.Arguments[6]) : (ushort)1;
            module.RegionVision.Show(target.Id, "operator", new SceneBounds(x, y, width, height), tile);
            return ValueTask.FromResult(Info("Personal region border shown."));
        }

        return ValueTask.FromResult(Failure(
            "command.scene.usage",
            "Usage: scene list | scene border <player> <x> <y> <width> <height> [tile] | scene clear <player>"));
    }

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(
        MessageDescriptor.Info("command.scene.info").WithDefaultText(text));

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}
