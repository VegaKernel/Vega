using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Operations;

/// <summary>
/// Observes Terraria's native startup status and arbitrates console ownership
/// once the Terminal UI has initialized successfully.
/// </summary>
public sealed class TerrariaStartupProgress :
    IStartupProgressReader,
    IUnhandledOperationsCommandSink,
    IDisposable
{
    private static readonly Regex PercentagePattern = new(
        @"(?<value>[0-9]+(?:[.,][0-9]+)?)%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IOperationsLogger? logger;

    private StartupProgressSnapshot snapshot = new(
        "Preparing Terraria world",
        null,
        string.Empty,
        DateTimeOffset.UtcNow);
    private Func<bool>? terminalUiActive;
    private readonly object consoleSync = new();
    private TextWriter? originalOutput;
    private TextWriter? originalError;
    private UiOwnedTextWriter? ownedOutput;
    private UiOwnedTextWriter? ownedError;
    private readonly BlockingCollection<string> nativeCommands = new();
    private bool attached;

    public TerrariaStartupProgress(IOperationsLogger? logger = null)
    {
        this.logger = logger;
    }

    public void Attach(Func<bool> terminalUiActive)
    {
        ArgumentNullException.ThrowIfNull(terminalUiActive);
        if (attached)
        {
            return;
        }

        this.terminalUiActive = terminalUiActive;
        global::HookEvents.Terraria.Main.set_statusText += OnStatusTextChanged;
        global::HookEvents.Terraria.Main.ReadLineInput += OnReadLineInput;
        attached = true;
    }

    public StartupProgressSnapshot Read() => Volatile.Read(ref snapshot);

    public void AcquireConsoleOwnership(int uiThreadId)
    {
        lock (consoleSync)
        {
            if (ownedOutput is not null)
            {
                return;
            }

            originalOutput = Console.Out;
            originalError = Console.Error;
            ownedOutput = new UiOwnedTextWriter(
                originalOutput,
                uiThreadId,
                line => CaptureNativeOutput(OperationsLogLevel.Information, line));
            ownedError = new UiOwnedTextWriter(
                originalError,
                uiThreadId,
                line => CaptureNativeOutput(OperationsLogLevel.Warning, line));
            Console.SetOut(ownedOutput);
            Console.SetError(ownedError);
        }
    }

    public void ReleaseConsoleOwnership(bool restartDedicatedInput)
    {
        UiOwnedTextWriter? output;
        UiOwnedTextWriter? error;
        lock (consoleSync)
        {
            output = ownedOutput;
            error = ownedError;
            if (ownedOutput is not null && ReferenceEquals(Console.Out, ownedOutput))
            {
                Console.SetOut(originalOutput!);
            }

            if (ownedError is not null && ReferenceEquals(Console.Error, ownedError))
            {
                Console.SetError(originalError!);
            }

            ownedOutput = null;
            ownedError = null;
            originalOutput = null;
            originalError = null;
        }

        output?.FlushCaptured();
        error?.FlushCaptured();

        if (restartDedicatedInput)
        {
            nativeCommands.TryAdd(string.Empty);
        }
    }

    public ValueTask SubmitAsync(
        string commandText,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(commandText))
        {
            throw new ArgumentException("Command text must not be empty.", nameof(commandText));
        }

        nativeCommands.Add(commandText, cancellationToken);
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        ReleaseConsoleOwnership(restartDedicatedInput: false);
        if (!attached)
        {
            return;
        }

        global::HookEvents.Terraria.Main.set_statusText -= OnStatusTextChanged;
        global::HookEvents.Terraria.Main.ReadLineInput -= OnReadLineInput;
        terminalUiActive = null;
        attached = false;
        nativeCommands.Dispose();
    }

    private void OnStatusTextChanged(
        object? sender,
        global::HookEvents.Terraria.Main.set_statusTextEventArgs args)
    {
        string rawText = args.value?.Trim() ?? string.Empty;
        Match percentage = PercentagePattern.Match(rawText);
        double? fraction = null;
        if (percentage.Success && double.TryParse(
                percentage.Groups["value"].Value.Replace(',', '.'),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double parsed))
        {
            fraction = Math.Clamp(parsed / 100d, 0d, 1d);
        }

        string activity = ExtractActivity(rawText);
        Volatile.Write(ref snapshot, new StartupProgressSnapshot(
            activity,
            fraction,
            rawText,
            DateTimeOffset.UtcNow));

    }

    private void OnReadLineInput(
        object? sender,
        global::HookEvents.Terraria.Main.ReadLineInputEventArgs args)
    {
        if (terminalUiActive?.Invoke() == true)
        {
            // Terraria keeps its normal command parser, but its input thread reads
            // from the TUI queue instead of competing for Console.ReadLine.
            args.ContinueExecution = false;
            args.HookReturnValue = nativeCommands.Take();
        }
    }

    private static string ExtractActivity(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return "Loading Terraria world";
        }

        string[] segments = rawText.Split(" - ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string activity = segments.FirstOrDefault(segment => !PercentagePattern.IsMatch(segment)) ?? rawText;
        activity = PercentagePattern.Replace(activity, string.Empty).Trim(' ', '-', ':');
        return string.IsNullOrWhiteSpace(activity) ? "Loading Terraria world" : activity;
    }

    private void CaptureNativeOutput(OperationsLogLevel level, string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            logger?.Log(level, "terraria-console", line);
        }
    }

    private sealed class UiOwnedTextWriter(
        TextWriter inner,
        int uiThreadId,
        Action<string> capture) : TextWriter
    {
        private const int MaxCapturedLineLength = 8_192;
        private readonly object sync = new();
        private readonly StringBuilder pending = new();
        private bool truncated;

        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            if (Environment.CurrentManagedThreadId == uiThreadId)
            {
                inner.Write(value);
                return;
            }

            Capture(value.ToString());
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (Environment.CurrentManagedThreadId == uiThreadId)
            {
                inner.Write(buffer, index, count);
                return;
            }

            Capture(new string(buffer, index, count));
        }

        public override void Write(string? value)
        {
            if (Environment.CurrentManagedThreadId == uiThreadId)
            {
                inner.Write(value);
                return;
            }

            Capture(value);
        }

        public override void WriteLine(string? value)
        {
            if (Environment.CurrentManagedThreadId == uiThreadId)
            {
                inner.WriteLine(value);
                return;
            }

            Capture(value);
            Capture(Environment.NewLine);
        }

        public override void Flush()
        {
            if (Environment.CurrentManagedThreadId == uiThreadId)
            {
                inner.Flush();
                return;
            }

            FlushCaptured();
        }

        public void FlushCaptured()
        {
            string? line = null;
            lock (sync)
            {
                if (pending.Length > 0)
                {
                    line = CompleteLine();
                    pending.Clear();
                    truncated = false;
                }
            }

            if (line is not null)
            {
                capture(line);
            }
        }

        private void Capture(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            List<string>? lines = null;
            lock (sync)
            {
                foreach (char character in value)
                {
                    if (character == '\n')
                    {
                        lines ??= [];
                        lines.Add(CompleteLine());
                        pending.Clear();
                        truncated = false;
                    }
                    else if (character != '\r' && pending.Length < MaxCapturedLineLength)
                    {
                        pending.Append(character);
                    }
                    else if (character != '\r')
                    {
                        truncated = true;
                    }
                }
            }

            if (lines is not null)
            {
                foreach (string line in lines)
                {
                    capture(line);
                }
            }
        }

        private string CompleteLine() => truncated
            ? $"{pending} ... [truncated]"
            : pending.ToString();
    }
}
