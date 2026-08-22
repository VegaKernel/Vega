using OTAPI;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Localization;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Routes server-console commands owned by Vega into the modern command dispatcher.
/// Unknown commands remain available to Terraria's normal console path.
/// </summary>
internal sealed class NativeConsoleCommandHooks : IDisposable
{
    private readonly ICommandDispatcher dispatcher;
    private readonly ILocalizationService localization;
    private readonly IPlayerCultureService cultures;
    private bool attached;

    public NativeConsoleCommandHooks(
        ICommandDispatcher dispatcher,
        ILocalizationService localization,
        IPlayerCultureService cultures)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.localization = localization ?? throw new ArgumentNullException(nameof(localization));
        this.cultures = cultures ?? throw new ArgumentNullException(nameof(cultures));
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        Hooks.Main.CommandProcess += OnCommandProcess;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached)
        {
            return;
        }

        Hooks.Main.CommandProcess -= OnCommandProcess;
        attached = false;
    }

    private void OnCommandProcess(object? sender, Hooks.Main.CommandProcessEventArgs args)
    {
        if (args.Result == HookResult.Cancel ||
            !CommandTextParser.TryParse(args.Command, out ParsedCommandText command) ||
            !dispatcher.CanDispatch(command.Name))
        {
            return;
        }

        // The OTAPI hook is synchronous. Claim the known Vega command, then observe its
        // ValueTask asynchronously instead of blocking the console thread.
        args.Result = HookResult.Cancel;
        _ = ExecuteAsync(command);
    }

    private async Task ExecuteAsync(ParsedCommandText command)
    {
        try
        {
            var invocation = new CommandInvocation(
                command.Name,
                command.Arguments,
                ServerCommandPrincipal.Instance,
                ConsoleCommandOutput.Instance,
                command.Silent);

            var result = await dispatcher.DispatchAsync(invocation).ConfigureAwait(false);
            WriteResult(result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vega console command '{command.Name}' failed: {ex}");
        }
    }

    private void WriteResult(CommandExecutionResult result)
    {
        if (result.Messages is { Count: > 0 })
        {
            foreach (var message in result.Messages)
            {
                WriteMessage(message);
            }

            return;
        }

        if (result.Status != CommandExecutionStatus.Success && !string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            Console.Error.WriteLine($"Vega command failed: {result.ErrorCode}");
        }
    }

    private void WriteMessage(MessageDescriptor message)
    {
        string text = localization.Format(message, cultures.ServerCulture);
        if (message.Severity is MessageSeverity.Warning or MessageSeverity.Error)
        {
            Console.Error.WriteLine(text);
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    private sealed class ServerCommandPrincipal : ICommandPrincipal
    {
        public static ServerCommandPrincipal Instance { get; } = new();

        public string Name => "Server";

        public bool IsServer => true;

        public bool HasPermission(string permission) => true;
    }

    private sealed class ConsoleCommandOutput : ICommandOutput
    {
        public static ConsoleCommandOutput Instance { get; } = new();

        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default)
        {
            Console.WriteLine(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default)
        {
            Console.WriteLine(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default)
        {
            Console.Error.WriteLine(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default)
        {
            Console.Error.WriteLine(message);
            return ValueTask.CompletedTask;
        }
    }
}
