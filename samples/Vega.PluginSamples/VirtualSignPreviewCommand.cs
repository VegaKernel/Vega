using Vega.Contracts.Commands;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.PluginSdk;

namespace Vega.PluginSamples;

// A personal virtual sign; stock UI when available, chat fallback otherwise.
internal sealed class VirtualSignPreviewCommand : ICommandHandler
{
    private readonly IVegaPluginContext plugin;
    private readonly object sync = new();
    private IPluginTileProvider? provider;
    private IPluginVirtualSign? sign;
    private PlayerSessionId? session;

    public VirtualSignPreviewCommand(IVegaPluginContext plugin)
    {
        this.plugin = plugin;
        plugin.Tasks.Every("sample-sign-read", TimeSpan.FromMilliseconds(250), Poll);
        plugin.Players.OnLeft(closed => { lock (sync) if (session == closed.SessionId) Clear(); });
    }

    public CommandDescriptor Descriptor { get; } = new("samplesign", [], ["vega.samples.world"],
        Syntax: "samplesign <player> <tileX> <tileY> [--edit] <text> | clear");

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
            if (args.Count < 4 || !plugin.World.FakeProvider.SupportsVirtualSigns ||
                !plugin.Players.TryFindExact(args[0], out var player) || player is not { SessionId: not null } ||
                !int.TryParse(args[1], out int x) || !int.TryParse(args[2], out int y) || x < 0 || y < 0)
                return ValueTask.FromResult(CommandExecutionResult.Failed("command.invalid_usage"));
            bool allowEdits = args[3].Equals("--edit", StringComparison.OrdinalIgnoreCase);
            if (allowEdits && (args.Count < 5 || !plugin.World.FakeProvider.SupportsVirtualSignEdits))
                return ValueTask.FromResult(CommandExecutionResult.Failed("command.invalid_usage"));
            string text = string.Join(' ', args.Skip(allowEdits ? 4 : 3));
            if (text.Length > SceneVirtualSignDefinition.MaximumTextLength) return ValueTask.FromResult(CommandExecutionResult.Failed("command.invalid_usage"));
            var tiles = new SceneTileArea(2, 2,
            [
                new(55, FrameX: 0, FrameY: 0, Flags: SceneTileFlags.Active),
                new(55, FrameX: 18, FrameY: 0, Flags: SceneTileFlags.Active),
                new(55, FrameX: 0, FrameY: 18, Flags: SceneTileFlags.Active),
                new(55, FrameX: 18, FrameY: 18, Flags: SceneTileFlags.Active)
            ]);
            var next = plugin.World.FakeProvider.CreatePersonal("logical sign", new(x, y, 2, 2), tiles,
                [SceneSessionObserver.From(player)], layer: 100);
            try
            {
                var nextSign = next.CreateVirtualSign(0, 0, text, allowEdits);
                Clear();
                provider = next;
                sign = nextSign;
                session = player.SessionId;
            }
            catch { next.Dispose(); throw; }
            return ValueTask.FromResult(CommandExecutionResult.Success);
        }
    }

    private void Poll()
    {
        lock (sync)
        {
            if (sign is null) return;
            // The operator explicitly opted in; this personal sample accepts that observer's edits.
            if (sign.TryTakeEditRequest(out var edit) && edit is { } change && change.Observer.Session == session)
                sign.TryAcceptEdit(change);
            if (!sign.TryTakeReadRequest(out var pending) || pending is not { } request ||
                !plugin.Players.TryGet(request.Observer.Player, out var player) || player is null ||
                player.SessionId != request.Observer.Session || player.WorldId != request.Sign.WorldId) return;
            if (!sign.TryPresentRead(request)) plugin.Players.Message(player, request.Sign.Definition.Text);
        }
    }

    private void Clear()
    {
        provider?.Dispose();
        provider = null;
        sign = null;
        session = null;
    }
}
