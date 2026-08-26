using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

/// <summary>
/// One persisted admission state for the ServerLock-style /lock command and the IP whitelist.
/// Locking the server makes the IPv4 allowlist mandatory for new sessions. The whitelist can also
/// be enabled independently without locking the server.
/// </summary>
public sealed class FileServerAccessStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object gate = new();
    private readonly string path;
    private ServerAccessState state;

    public FileServerAccessStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        state = Load(this.path);
    }

    public bool IsLocked
    {
        get
        {
            lock (gate) return state.Locked;
        }
    }

    public bool IsWhitelistEnabled
    {
        get
        {
            lock (gate) return state.WhitelistEnabled;
        }
    }

    public bool IsAdmissionRestricted
    {
        get
        {
            lock (gate) return state.Locked || state.WhitelistEnabled;
        }
    }

    public IReadOnlyList<string> SnapshotEntries()
    {
        lock (gate)
        {
            return Array.AsReadOnly(state.Entries.ToArray());
        }
    }

    public bool SetLocked(bool locked)
    {
        lock (gate)
        {
            if (state.Locked == locked) return false;
            ServerAccessState next = state with { Locked = locked };
            Save(next);
            state = next;
            return true;
        }
    }

    public bool ToggleLocked()
    {
        lock (gate)
        {
            ServerAccessState next = state with { Locked = !state.Locked };
            Save(next);
            state = next;
            return next.Locked;
        }
    }

    public bool SetWhitelistEnabled(bool enabled)
    {
        lock (gate)
        {
            if (state.WhitelistEnabled == enabled) return false;
            ServerAccessState next = state with { WhitelistEnabled = enabled };
            Save(next);
            state = next;
            return true;
        }
    }

    public bool TryAdd(string value, out string? normalized, out string? error)
    {
        if (!TryNormalizeIpv4Network(value, out normalized, out error)) return false;

        lock (gate)
        {
            if (state.Entries.Contains(normalized!, StringComparer.OrdinalIgnoreCase))
            {
                error = "The IPv4 address or network is already whitelisted.";
                return false;
            }

            string[] entries = [.. state.Entries, normalized!];
            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            ServerAccessState next = state with { Entries = entries };
            Save(next);
            state = next;
            return true;
        }
    }

    public bool TryRemove(string value, out string? normalized, out string? error)
    {
        if (!TryNormalizeIpv4Network(value, out normalized, out error)) return false;

        lock (gate)
        {
            int index = Array.FindIndex(
                state.Entries,
                entry => string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                error = "The IPv4 address or network is not whitelisted.";
                return false;
            }

            string[] entries = state.Entries.Where((_, candidate) => candidate != index).ToArray();
            ServerAccessState next = state with { Entries = entries };
            Save(next);
            state = next;
            return true;
        }
    }

    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (gate)
        {
            if (!state.Locked && !state.WhitelistEnabled) return true;
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;

            foreach (string entry in state.Entries)
            {
                if (TryParseNormalized(entry, out IPAddress? network, out int prefixLength) &&
                    Contains(network!, prefixLength, address))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public static bool TryNormalizeIpv4Network(
        string value,
        out string? normalized,
        out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Whitelist entry is empty.";
            return false;
        }

        string[] parts = value.Trim().Split('/', 2, StringSplitOptions.TrimEntries);
        if (!IPAddress.TryParse(parts[0], out IPAddress? parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "Whitelist entry must be a valid IPv4 address, optionally followed by a CIDR prefix.";
            return false;
        }

        int prefixLength = 32;
        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], out prefixLength) || prefixLength < 0 || prefixLength > 32))
        {
            error = "IPv4 CIDR prefix must be between 0 and 32.";
            return false;
        }

        byte[] bytes = parsed.GetAddressBytes();
        ApplyMask(bytes, prefixLength);
        normalized = $"{new IPAddress(bytes)}/{prefixLength}";
        return true;
    }

    private static ServerAccessState Load(string path)
    {
        if (!File.Exists(path)) return new ServerAccessState(false, false, []);

        try
        {
            PersistedServerAccess? document = JsonSerializer.Deserialize<PersistedServerAccess>(
                File.ReadAllText(path), JsonOptions);
            var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in document?.Entries ?? [])
            {
                if (!TryNormalizeIpv4Network(raw, out string? normalized, out string? error))
                {
                    throw new InvalidDataException($"Invalid server access entry '{raw}': {error}");
                }

                entries.Add(normalized!);
            }

            return new ServerAccessState(
                document?.Locked ?? false,
                document?.WhitelistEnabled ?? false,
                entries.OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Server access state '{path}' is not valid JSON.", ex);
        }
    }

    private void Save(ServerAccessState next)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(
                    new PersistedServerAccess(next.Locked, next.WhitelistEnabled, next.Entries),
                    JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static bool TryParseNormalized(string value, out IPAddress? network, out int prefixLength)
    {
        network = null;
        prefixLength = 0;
        string[] parts = value.Split('/', 2);
        return parts.Length == 2 &&
            IPAddress.TryParse(parts[0], out network) &&
            network.AddressFamily == AddressFamily.InterNetwork &&
            int.TryParse(parts[1], out prefixLength) &&
            prefixLength is >= 0 and <= 32;
    }

    private static bool Contains(IPAddress network, int prefixLength, IPAddress address)
    {
        if (network.AddressFamily != AddressFamily.InterNetwork ||
            address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] candidate = address.GetAddressBytes();
        ApplyMask(candidate, prefixLength);
        return candidate.AsSpan().SequenceEqual(network.GetAddressBytes());
    }

    private static void ApplyMask(byte[] bytes, int prefixLength)
    {
        int completeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;
        if (remainingBits > 0)
        {
            bytes[completeBytes] &= (byte)(0xff << (8 - remainingBits));
            completeBytes++;
        }

        Array.Clear(bytes, completeBytes, bytes.Length - completeBytes);
    }

    private sealed record PersistedServerAccess(
        bool Locked = false,
        bool WhitelistEnabled = false,
        string[]? Entries = null);

    private sealed record ServerAccessState(bool Locked, bool WhitelistEnabled, string[] Entries);
}

public sealed class LockCommand(FileServerAccessStore access) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "lock",
        Array.Empty<string>(),
        ["vega.cfg.lock"],
        Syntax: "lock [on|off|status]");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Arguments.Count > 1) return ValueTask.FromResult(Usage());

        try
        {
            if (invocation.Arguments.Count == 0)
            {
                bool locked = access.ToggleLocked();
                return ValueTask.FromResult(State(locked));
            }

            string action = invocation.Arguments[0].Trim().ToLowerInvariant();
            return ValueTask.FromResult(action switch
            {
                "on" or "enable" => Set(true),
                "off" or "disable" => Set(false),
                "status" => State(access.IsLocked),
                _ => Usage()
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Failure("command.lock.persist-failed", ex.Message));
        }
    }

    private CommandExecutionResult Set(bool locked)
    {
        access.SetLocked(locked);
        return State(locked);
    }

    private static CommandExecutionResult State(bool locked) =>
        CommandExecutionResult.WithMessages(
            MessageDescriptor.Success("command.lock.state", locked ? "locked" : "unlocked")
                .WithDefaultText("Server is {0}."));

    private static CommandExecutionResult Usage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.lock.usage",
        [MessageDescriptor.Error("command.lock.usage")
            .WithDefaultText("Usage: lock [on|off|status].")]);

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

public sealed class WhitelistCommand(FileServerAccessStore access) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(
        "whitelist",
        Array.Empty<string>(),
        ["vega.cfg.whitelist"],
        Syntax: "whitelist <ipv4[/cidr]> | whitelist add|del <ipv4[/cidr]> | whitelist list|status|on|off");

    public ValueTask<CommandExecutionResult> ExecuteAsync(
        CommandInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommandExecutionResult result;

        if (invocation.Arguments.Count == 0)
        {
            result = Status();
        }
        else if (invocation.Arguments.Count == 1)
        {
            string action = invocation.Arguments[0].Trim().ToLowerInvariant();
            result = action switch
            {
                "list" => List(),
                "status" => Status(),
                "on" or "enable" => SetEnabled(true),
                "off" or "disable" => SetEnabled(false),
                _ => Add(invocation.Arguments[0])
            };
        }
        else if (invocation.Arguments.Count == 2)
        {
            string action = invocation.Arguments[0].Trim().ToLowerInvariant();
            result = action switch
            {
                "add" => Add(invocation.Arguments[1]),
                "del" or "delete" or "remove" => Remove(invocation.Arguments[1]),
                _ => InvalidUsage()
            };
        }
        else
        {
            result = InvalidUsage();
        }

        return ValueTask.FromResult(result);
    }

    private CommandExecutionResult Add(string value)
    {
        try
        {
            return access.TryAdd(value, out string? normalized, out string? error)
                ? CommandExecutionResult.WithMessages(
                    MessageDescriptor.Success("command.whitelist.added", normalized)
                        .WithDefaultText("Added {0} to the whitelist."))
                : Failure("command.whitelist.add-failed", error ?? "Unable to add whitelist entry.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure("command.whitelist.persist-failed", ex.Message);
        }
    }

    private CommandExecutionResult Remove(string value)
    {
        try
        {
            return access.TryRemove(value, out string? normalized, out string? error)
                ? CommandExecutionResult.WithMessages(
                    MessageDescriptor.Success("command.whitelist.removed", normalized)
                        .WithDefaultText("Removed {0} from the whitelist."))
                : Failure("command.whitelist.remove-failed", error ?? "Unable to remove whitelist entry.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure("command.whitelist.persist-failed", ex.Message);
        }
    }

    private CommandExecutionResult SetEnabled(bool enabled)
    {
        try
        {
            access.SetWhitelistEnabled(enabled);
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Success("command.whitelist.state", enabled ? "enabled" : "disabled")
                    .WithDefaultText("Whitelist is now {0}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure("command.whitelist.persist-failed", ex.Message);
        }
    }

    private CommandExecutionResult Status() =>
        CommandExecutionResult.WithMessages(
            MessageDescriptor.Info(
                    "command.whitelist.status",
                    access.IsWhitelistEnabled ? "enabled" : "disabled",
                    access.IsLocked ? "locked" : "unlocked",
                    access.SnapshotEntries().Count)
                .WithDefaultText("Whitelist is {0}; server is {1}; {2} entries."));

    private CommandExecutionResult List()
    {
        IReadOnlyList<string> entries = access.SnapshotEntries();
        if (entries.Count == 0)
        {
            return CommandExecutionResult.WithMessages(
                MessageDescriptor.Info("command.whitelist.list-empty")
                    .WithDefaultText("The whitelist is empty."));
        }

        return CommandExecutionResult.WithMessages(entries.Select(entry =>
            MessageDescriptor.Info("command.whitelist.list-entry", entry)
                .WithDefaultText("{0}"))
            .ToArray());
    }

    private static CommandExecutionResult InvalidUsage() => new(
        CommandExecutionStatus.InvalidUsage,
        "command.whitelist.usage",
        [MessageDescriptor.Error("command.whitelist.usage")
            .WithDefaultText("Usage: whitelist <ipv4[/cidr]> | whitelist add|del <ipv4[/cidr]> | whitelist list|status|on|off.")]);

    private static CommandExecutionResult Failure(string code, string text) =>
        CommandExecutionResult.Failed(code, MessageDescriptor.Error(code).WithDefaultText(text));
}

public sealed class PersistentServerAccessEnforcer : IDisposable
{
    private readonly FileServerAccessStore access;
    private readonly IPlayerDirectory players;
    private readonly IPlayerOperations playerOperations;
    private readonly IPlayerSessionLifecycle sessions;
    private readonly IOperationsLogger logger;
    private readonly CancellationTokenSource shutdown = new();

    public PersistentServerAccessEnforcer(
        FileServerAccessStore access,
        IPlayerDirectory players,
        IPlayerOperations playerOperations,
        IPlayerSessionLifecycle sessions,
        IOperationsLogger logger)
    {
        this.access = access ?? throw new ArgumentNullException(nameof(access));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.playerOperations = playerOperations ?? throw new ArgumentNullException(nameof(playerOperations));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        sessions.Opened += OnSessionOpened;
    }

    public void Dispose()
    {
        sessions.Opened -= OnSessionOpened;
        shutdown.Cancel();
        shutdown.Dispose();
    }

    private void OnSessionOpened(PlayerSessionOpened opened)
    {
        if (!access.IsAdmissionRestricted) return;
        _ = Task.Run(() => EnforceAsync(opened), shutdown.Token);
    }

    private async Task EnforceAsync(PlayerSessionOpened opened)
    {
        try
        {
            players.TryGet(opened.PlayerId, out PlayerSnapshot? player);
            bool hasIpv4 = BanCommand.TryGetIpAddress(player?.RemoteEndpoint, out string? rawAddress) &&
                IPAddress.TryParse(rawAddress, out IPAddress? address) &&
                address.AddressFamily == AddressFamily.InterNetwork;
            if (hasIpv4 && access.IsAllowed(address!)) return;

            string reason = access.IsLocked
                ? "Server is locked. Your IPv4 address is not whitelisted."
                : "Your IPv4 address is not on the whitelist.";
            PlayerOperationResult result = await playerOperations
                .KickAsync(opened.PlayerId, reason, shutdown.Token)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                logger.Log(
                    OperationsLogLevel.Warning,
                    "server-access",
                    $"Admission rejected '{opened.PlayerName}', but the connection could not be disconnected ({result.ErrorCode ?? "unknown"}).");
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "server-access",
                $"Failed to enforce server admission for '{opened.PlayerName}'.",
                ex);
        }
    }
}
