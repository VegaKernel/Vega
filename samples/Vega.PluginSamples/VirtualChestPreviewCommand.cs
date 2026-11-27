using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.PluginSdk;

namespace Vega.PluginSamples;

// Example: default read-only chat view; the explicit managed-GUI mode is for
// plugin-owned minigame inventory sessions, not ordinary unrestricted gameplay.
internal sealed class VirtualChestPreviewCommand : ICommandHandler
{
    private readonly IVegaPluginContext plugin;
    private readonly object sync = new();
    private IPluginTileProvider? provider;
    private IPluginVirtualChest? chest;
    private PlayerSessionId? session;
    private bool managedGui;

    public VirtualChestPreviewCommand(IVegaPluginContext plugin)
    {
        this.plugin = plugin;
        plugin.Tasks.Every("sample-chest-open", TimeSpan.FromMilliseconds(250), Poll);
        plugin.Players.OnLeft(closed => { lock (sync) if (session == closed.SessionId) Clear(); });
    }

    public CommandDescriptor Descriptor { get; } = new("samplechest", [], ["vega.samples.world"],
        Syntax: "samplechest <player> <tileX> <tileY> [--managed-gui] | clear");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        lock (sync)
        {
            if (args.Count == 1 && args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                return ValueTask.FromResult(CommandExecutionResult.Success);
            }
            bool useManagedGui = args.Count == 4 && args[3].Equals("--managed-gui", StringComparison.OrdinalIgnoreCase);
            if ((args.Count != 3 && !useManagedGui) || !plugin.World.FakeProvider.SupportsVirtualChests ||
                useManagedGui && !plugin.World.FakeProvider.SupportsManagedVirtualChestGui ||
                !plugin.Players.TryFindExact(args[0], out var player) || player is not { SessionId: not null } ||
                !int.TryParse(args[1], out int x) || !int.TryParse(args[2], out int y) || x < 0 || y < 0)
                return ValueTask.FromResult(CommandExecutionResult.Failed("command.invalid_usage"));
            var tiles = new SceneTileArea(2, 2,
            [
                new(21, FrameX: 0, FrameY: 0, Flags: SceneTileFlags.Active),
                new(21, FrameX: 18, FrameY: 0, Flags: SceneTileFlags.Active),
                new(21, FrameX: 0, FrameY: 18, Flags: SceneTileFlags.Active),
                new(21, FrameX: 18, FrameY: 18, Flags: SceneTileFlags.Active)
            ]);
            var next = plugin.World.FakeProvider.CreatePersonal("logical chest", new(x, y, 2, 2), tiles,
                [SceneSessionObserver.From(player)], layer: 100);
            try
            {
                var nextChest = next.CreateVirtualChest(0, 0, "Preview rewards", [new(0, 1, 1, 0)]);
                Clear();
                provider = next;
                chest = nextChest;
                session = player.SessionId;
                managedGui = useManagedGui;
            }
            catch { next.Dispose(); throw; }
            return ValueTask.FromResult(CommandExecutionResult.Success);
        }
    }

    private void Poll()
    {
        // SDK Every runs on the game thread. Recheck the exact session before
        // sending a message to a reusable player slot.
        lock (sync)
        {
            if (chest is null) return;
            if (managedGui && chest.TryTakeManagedGuiTransfer(out var movement) && movement is { } intent && intent.Transfer.Open.Observer.Session == session)
                chest.AcceptManagedGuiTransfer(intent);
            if (!chest.TryTakeOpenRequest(out var pending) || pending is not { } request ||
                !plugin.Players.TryGet(request.Observer.Player, out var player) || player is null ||
                player.SessionId != request.Observer.Session || player.WorldId != request.Chest.Identity.WorldId) return;
            if (managedGui)
            {
                plugin.Players.Message(player,"Managed GUI uses server-controlled inventory until you reconnect.");
                if (chest.TryPresentManagedGui(request)) return;
            }
            var contents = request.Chest.Definition;
            plugin.Players.Message(player, $"{contents.Name}: " + string.Join(", ", contents.Items
                .Where(item => item.ItemType != 0).Select(item => $"item {item.ItemType} x{item.Stack}")));
        }
    }

    private void Clear()
    {
        provider?.Dispose();
        provider = null;
        chest = null;
        session = null;
        managedGui = false;
    }
}
