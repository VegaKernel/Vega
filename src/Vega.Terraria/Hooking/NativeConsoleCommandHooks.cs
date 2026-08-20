using System.Globalization;
using OTAPI;
using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Messaging;

namespace Vega.Terraria.Hooking;

/// <summary>
/// Routes server-console commands owned by Vega into the modern command dispatcher before the legacy TSAPI bridge.
/// Unknown commands continue through the legacy path while migration is incomplete.
/// </summary>
internal sealed class NativeConsoleCommandHooks : IDisposable
{
    private readonly ICommandDispatcher dispatcher;
    private bool attached;

    public NativeConsoleCommandHooks(ICommandDispatcher dispatcher)
    {
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
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
            !TryParseCommand(args.Command, out var name, out var arguments, out var silent) ||
            !dispatcher.CanDispatch(name))
        {
            return;
        }

        // The OTAPI hook is synchronous. Claim the known Vega command before the legacy bridge,
        // then observe its ValueTask asynchronously instead of blocking the console thread.
        args.Result = HookResult.Cancel;
        _ = ExecuteAsync(name, arguments, silent);
    }

    private async Task ExecuteAsync(string name, IReadOnlyList<string> arguments, bool silent)
    {
        try
        {
            var invocation = new CommandInvocation(
                name,
                arguments,
                ServerCommandPrincipal.Instance,
                ConsoleCommandOutput.Instance,
                silent);

            var result = await dispatcher.DispatchAsync(invocation).ConfigureAwait(false);
            WriteResult(result);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vega console command '{name}' failed: {ex}");
        }
    }

    private static bool TryParseCommand(
        string command,
        out string name,
        out IReadOnlyList<string> arguments,
        out bool silent)
    {
        name = string.Empty;
        arguments = Array.Empty<string>();
        silent = false;

        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var text = command.Trim();
        if (text[0] is '/' or '.')
        {
            silent = text[0] == '.';
            text = text[1..].TrimStart();
        }

        if (text.Length == 0)
        {
            return false;
        }

        var separator = text.IndexOfAny([' ', '\t']);
        if (separator < 0)
        {
            name = text;
            return true;
        }

        name = text[..separator];
        arguments = text[(separator + 1)..]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return name.Length > 0;
    }

    private static void WriteResult(CommandExecutionResult result)
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

    private static void WriteMessage(MessageDescriptor message)
    {
        string text;
        if (message.DefaultText is not null)
        {
            try
            {
                text = string.Format(
                    CultureInfo.InvariantCulture,
                    message.DefaultText,
                    message.Arguments.ToArray());
            }
            catch (FormatException)
            {
                text = FormatKeyAndArguments(message);
            }
        }
        else
        {
            text = FormatKeyAndArguments(message);
        }

        if (message.Severity is MessageSeverity.Warning or MessageSeverity.Error)
        {
            Console.Error.WriteLine(text);
        }
        else
        {
            Console.WriteLine(text);
        }
    }

    private static string FormatKeyAndArguments(MessageDescriptor message) =>
        message.Arguments.Count == 0
            ? message.Key
            : $"{message.Key}: {string.Join(", ", message.Arguments.Select(static value => value?.ToString() ?? "<null>"))}";

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
