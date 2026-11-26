using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.PluginSdk;

namespace Vega.PluginSamples;

// Example: capture a real rectangle and display a private, movable copy without changing the world.
internal sealed class FakePreviewCommand : ICommandHandler
{
    private readonly IVegaPluginContext plugin;
    private readonly object sync = new();
    private IPluginTileProvider? preview;
    private PlayerSessionId? session;

    public FakePreviewCommand(IVegaPluginContext plugin)
    {
        this.plugin = plugin;
        plugin.Players.OnLeft(closed =>
        {
            lock (sync)
                if (session == closed.SessionId) Clear();
        });
    }

    public CommandDescriptor Descriptor { get; } = new("samplepreview", [], ["vega.samples.world"],
        Syntax: "samplepreview <player> <sourceX> <sourceY> <targetX> <targetY> <width> <height> | move <dx> <dy> | clear");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        if (args.Count == 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            lock (sync) Clear();
            return CommandExecutionResult.Success;
        }
        if (args.Count == 3 && args[0].Equals("move", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(args[1], out int dx) && int.TryParse(args[2], out int dy))
        {
            lock (sync)
            {
                if (preview is null) return CommandExecutionResult.Failed("samplepreview.not_found");
                preview.Move(dx, dy);
                return CommandExecutionResult.Success;
            }
        }
        if (args.Count != 7 || !plugin.World.FakeProvider.IsAvailable ||
            !plugin.Players.TryFindExact(args[0], out var player) || player is not { SessionId: not null } ||
            !int.TryParse(args[1], out int sx) || !int.TryParse(args[2], out int sy) ||
            !int.TryParse(args[3], out int tx) || !int.TryParse(args[4], out int ty) ||
            !int.TryParse(args[5], out int w) || !int.TryParse(args[6], out int h) || w is < 1 or > 64 || h is < 1 or > 64)
            return CommandExecutionResult.Failed("command.invalid_usage");

        var source = await plugin.World.Tiles.CaptureAsync(new SceneBounds(sx, sy, w, h), cancellationToken);
        if (source is null) return CommandExecutionResult.Failed("world.tiles.not_found");
        lock (sync)
        {
            if (!plugin.Players.TryGet(player.Id, out var current) || current?.SessionId != player.SessionId)
                return CommandExecutionResult.Failed("player.not_found");
            var next = plugin.World.FakeProvider.CreatePersonal("clipboard", new SceneBounds(tx, ty, w, h),
                source.Tiles, [SceneSessionObserver.From(player)], layer: 100);
            Clear();
            preview = next;
            session = player.SessionId;
        }
        return CommandExecutionResult.Success;
    }

    private void Clear()
    {
        preview?.Dispose();
        preview = null;
        session = null;
    }
}
