using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Scenes;
using Vega.Contracts.Threading;

namespace Vega.Modules.Scenes;

public sealed class FakeProviderCommand(FakeProviderService providers, IPlayerDirectory players, IGameThreadDispatcher dispatcher, bool personal) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(personal ? "pfake" : "fake", [], ["vega.scenes.manage"],
        Syntax: personal
            ? "pfake <list [page]|info <name>|create <name> <player> <x> <y> <w> <h> [air|stone]|move <name> <dx> <dy>|layer <name> <layer>|enable|disable|remove <name>>"
            : "fake <list [page]|info <name>|create <name> <x> <y> <w> <h> [air|stone]|move <name> <dx> <dy>|layer <name> <layer>|enable|disable|remove <name>>");

    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return ValueTask.FromCanceled<CommandExecutionResult>(cancellationToken);
        if (dispatcher.IsGameThread) return ValueTask.FromResult(ExecuteSafely(invocation.Arguments));
        var completion = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.IsGameThreadKnown || !dispatcher.TryPost("fake-provider.command", () =>
        {
            if (cancellationToken.IsCancellationRequested) completion.TrySetCanceled(cancellationToken);
            else
            {
                try { completion.TrySetResult(ExecuteSafely(invocation.Arguments)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }
        })) return ValueTask.FromResult(Failure("Game thread is unavailable."));
        return new ValueTask<CommandExecutionResult>(completion.Task);
    }

    private CommandExecutionResult ExecuteSafely(IReadOnlyList<string> args)
    {
        try { return Execute(args); }
        catch (ObjectDisposedException) { return Failure("FakeProvider module is restarting or unavailable."); }
        catch (ArgumentException ex) { return Failure(ex.Message); }
        catch (OverflowException) { return Failure("Provider bounds exceed the supported coordinate range."); }
    }

    private CommandExecutionResult Execute(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return Failure(Descriptor.Syntax);
        string action = args[0].ToLowerInvariant();
        if (action == "list" && args.Count <= 2)
        {
            int page = 1;
            if (args.Count == 2 && (!int.TryParse(args[1], out page) || page < 1)) return Failure("Invalid page.");
            var all = providers.Snapshot(personal);
            var pages = Math.Max(1, (all.Count + 9) / 10);
            if (page > pages) return Failure($"Page must be between 1 and {pages}.");
            string entries = string.Join("; ", all.Skip((page - 1) * 10).Take(10).Select(Describe));
            return Info($"Providers ({page}/{pages}): {(entries.Length == 0 ? "none" : entries)}");
        }
        if (args.Count < 2) return Failure(Descriptor.Syntax);
        string name = args[1];
        if (action == "create")
        {
            int start = personal ? 3 : 2;
            if (args.Count != start + 4 && args.Count != start + 5) return Failure(Descriptor.Syntax);
            if (!int.TryParse(args[start], out int x) || !int.TryParse(args[start + 1], out int y) ||
                !int.TryParse(args[start + 2], out int width) || !int.TryParse(args[start + 3], out int height) ||
                x < 0 || y < 0 || width is < 1 or > 255 || height is < 1 or > 255) return Failure("Invalid bounds (width/height 1–255).");
            string fill = args.Count == start + 5 ? args[start + 4].ToLowerInvariant() : "air";
            if (fill is not ("air" or "stone")) return Failure("Fill must be air or stone.");
            PlayerId[]? observers = null;
            if (personal)
            {
                if (!players.TryFindExact(args[2], out var player) || player is not { SessionId: not null }) return Failure("Online player not found.");
                observers = [player.Id];
            }
            var tile = fill == "stone" ? new SceneTile(1, Flags: SceneTileFlags.Active) : SceneTile.Air;
            var tiles = SceneTileArea.Fill(width, height, tile);
            return providers.Create(name, new SceneBounds(x, y, width, height), tiles, observers)
                ? Info($"Provider '{name}' created.") : Failure("Provider name already exists in this world.");
        }
        if (action == "info" && args.Count == 2)
            return providers.TryGet(name, personal, out var definition) ? Info(Describe(definition!)) : Failure("Provider not found.");

        bool? updated = action switch
        {
            "move" when args.Count == 4 && int.TryParse(args[2], out int dx) && int.TryParse(args[3], out int dy) => providers.Move(name, personal, dx, dy),
            "layer" when args.Count == 3 && int.TryParse(args[2], out int order) => providers.SetLayer(name, personal, order),
            "enable" when args.Count == 2 => providers.SetEnabled(name, personal, true),
            "disable" when args.Count == 2 => providers.SetEnabled(name, personal, false),
            "remove" when args.Count == 2 => providers.Remove(name, personal),
            _ => null
        };
        return updated is true ? Info($"Provider '{name}' {action}: ok.") : Failure(updated is false ? "Provider not found." : Descriptor.Syntax);
    }

    private static string Describe(SceneOverlayDefinition definition) =>
        $"{definition.Name}: ({definition.Bounds.X},{definition.Bounds.Y}) {definition.Bounds.Width}x{definition.Bounds.Height}, layer={definition.Order}, enabled={definition.Enabled}, observers={(definition.Observers.IsGlobal ? "global" : definition.Observers.Players.Count.ToString())}";

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.fake.info").WithDefaultText(text));
    private static CommandExecutionResult Failure(string text) => CommandExecutionResult.Failed("command.fake.failed", MessageDescriptor.Error("command.fake.failed").WithDefaultText(text));
}
