using Vega.Contracts.Commands;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Scenes;
using Vega.PluginSdk;

namespace Vega.PluginSamples;

public sealed class UsabilityPlugin : VegaPlugin
{
    private IVegaPluginContext plugin = null!;
    private SampleSettings settings = new("Welcome to Vega!");
    private SampleState state = new(0);

    public override VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Samples.Usability",
        "1.0.0",
        ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        this.plugin = plugin;
        settings = plugin.Storage.LoadJson(
            "settings.json",
            () => new SampleSettings("Welcome to Vega!"),
            value => string.IsNullOrWhiteSpace(value.WelcomeMessage) ? "welcomeMessage is required" : null);
        state = plugin.Storage.LoadJson("state.json", () => new SampleState(0));

        plugin.Commands.Register(new WelcomeCommand(plugin));
        plugin.Commands.Register(new PlayerActionCommand(plugin));
        plugin.Commands.Register(new WorldCopyCommand(plugin));
        plugin.Commands.Register(new FakePreviewCommand(plugin));
        plugin.Commands.Register(new VirtualChestPreviewCommand(plugin));
        plugin.Commands.Register(new VirtualSignPreviewCommand(plugin));
        plugin.Commands.Register(new ChestRefillCommand(plugin));
        plugin.Players.OnJoined(OnJoined);
        plugin.Players.OnLeft(_ => SaveState());
        plugin.Tasks.Every("sample-heartbeat", TimeSpan.FromMinutes(1),
            () => plugin.Log.Write(OperationsLogLevel.Debug, $"Observed {state.JoinCount} joins."));
        plugin.Tasks.RunIo(
            "sample-startup-file-read",
            cancellationToken => ReadMotdAsync(plugin.Storage.DataDirectory, cancellationToken),
            motd => plugin.Log.Write(OperationsLogLevel.Information, motd));
    }

    protected override ValueTask OnStoppingAsync(CancellationToken cancellationToken)
    {
        SaveState();
        return ValueTask.CompletedTask;
    }

    private void OnJoined(Vega.Contracts.Players.PlayerSessionOpened player)
    {
        state = state with { JoinCount = state.JoinCount + 1 };
        plugin.Players.Message(player.PlayerId, settings.WelcomeMessage);
    }

    private void SaveState() => plugin.Storage.SaveJson("state.json", state);

    private static async ValueTask<string> ReadMotdAsync(string dataDirectory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataDirectory, "motd.txt");
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : "No plugin MOTD configured.";
    }

    private sealed class WelcomeCommand(IVegaPluginContext plugin) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(
            "welcome",
            ["hello"],
            ["vega.samples.welcome"],
            Syntax: "welcome [player]");

        public async ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (invocation.Arguments.Count == 0)
            {
                await invocation.Output.InfoAsync(
                    $"{plugin.Players.Online.Count} player(s) online.", cancellationToken);
                return CommandExecutionResult.Success;
            }

            if (!plugin.Players.TryFindExact(invocation.Arguments[0], out var player))
            {
                return CommandExecutionResult.Failed("player.not_found");
            }

            plugin.Players.Message(player!.Id, "An operator sent you a welcome message.");
            return CommandExecutionResult.Success;
        }
    }

    private sealed class PlayerActionCommand(IVegaPluginContext plugin) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(
            "sampleplayer",
            [],
            ["vega.samples.player"],
            Syntax: "sampleplayer <name> <message|kick|teleport>");

        public async ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (invocation.Arguments.Count != 2 ||
                !plugin.Players.TryFindExact(invocation.Arguments[0], out var player))
            {
                return CommandExecutionResult.Failed("player.not_found");
            }

            var result = invocation.Arguments[1].ToLowerInvariant() switch
            {
                "message" => await plugin.Players.MessageAsync(
                    player!.Id, "Hello from the usability sample.", cancellationToken),
                "kick" => await plugin.Players.KickAsync(
                    player!.Id, "Usability sample requested a kick.", cancellationToken),
                "teleport" => await plugin.Players.TeleportAsync(
                    player!.Id,
                    player.Position with { X = player.Position.X + 16 },
                    cancellationToken),
                _ => Vega.Contracts.Players.PlayerOperationResult.Failed("command.invalid_usage")
            };
            return result.IsSuccess
                ? CommandExecutionResult.Success
                : CommandExecutionResult.Failed(result.ErrorCode ?? "player.operation_failed");
        }
    }

    private sealed class WorldCopyCommand(IVegaPluginContext plugin) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(
            "sampleworldcopy",
            [],
            ["vega.samples.world"],
            Syntax: "sampleworldcopy <sourceX> <sourceY> <targetX> <targetY> <width> <height>");

        public async ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (invocation.Arguments.Count != 6 ||
                !int.TryParse(invocation.Arguments[0], out int sourceX) ||
                !int.TryParse(invocation.Arguments[1], out int sourceY) ||
                !int.TryParse(invocation.Arguments[2], out int targetX) ||
                !int.TryParse(invocation.Arguments[3], out int targetY) ||
                !int.TryParse(invocation.Arguments[4], out int width) ||
                !int.TryParse(invocation.Arguments[5], out int height) ||
                width <= 0 || height <= 0)
            {
                return CommandExecutionResult.Failed("command.invalid_usage");
            }

            var sourceBounds = new SceneBounds(sourceX, sourceY, width, height);
            var targetBounds = new SceneBounds(targetX, targetY, width, height);
            var source = await plugin.World.Tiles.CaptureAsync(sourceBounds, cancellationToken);
            var target = await plugin.World.Tiles.CaptureAsync(targetBounds, cancellationToken);
            if (source is null || target is null)
            {
                return CommandExecutionResult.Failed("world.tiles.not_found");
            }

            var result = await plugin.World.Tiles.ReplaceAsync(target, source.Tiles, cancellationToken);
            return result.IsSuccess
                ? CommandExecutionResult.Success
                : CommandExecutionResult.Failed(result.ErrorCode ?? "world.tiles.replace_failed");
        }
    }

    private sealed class ChestRefillCommand(IVegaPluginContext plugin) : ICommandHandler
    {
        public CommandDescriptor Descriptor { get; } = new(
            "samplechestrefill",
            [],
            ["vega.samples.chest"],
            Syntax: "samplechestrefill <x> <y> <stack>");

        public async ValueTask<CommandExecutionResult> ExecuteAsync(
            CommandInvocation invocation,
            CancellationToken cancellationToken)
        {
            if (invocation.Arguments.Count != 3 ||
                !int.TryParse(invocation.Arguments[0], out int x) ||
                !int.TryParse(invocation.Arguments[1], out int y) ||
                !int.TryParse(invocation.Arguments[2], out int stack) ||
                stack <= 0)
            {
                return CommandExecutionResult.Failed("command.invalid_usage");
            }

            var chest = await plugin.World.Chests.CaptureAsync(x, y, cancellationToken);
            if (chest is null)
            {
                return CommandExecutionResult.Failed("chest.not_found");
            }

            var items = chest.Items
                .Select(item => item.ItemType == 0 ? item : item with { Stack = stack })
                .ToArray();
            var result = await plugin.World.Chests.ReplaceAsync(
                chest,
                items,
                chest.Name,
                cancellationToken);
            return result.IsSuccess
                ? CommandExecutionResult.Success
                : CommandExecutionResult.Failed(result.ErrorCode ?? "chest.replace_failed");
        }
    }

    private sealed record SampleSettings(string WelcomeMessage);
    private sealed record SampleState(int JoinCount);
}
