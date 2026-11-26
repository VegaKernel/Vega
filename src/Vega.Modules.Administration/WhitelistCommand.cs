using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;

namespace Vega.Modules.Administration;

/// <summary>Deployment-wide IP allowlist, refreshed at admission rather than during gameplay.</summary>
public sealed class ConnectionWhitelist : IConnectionAdmissionPolicy
{
    private const int MaximumAddresses = 4096;
    private readonly string path;
    private readonly string lockPath;
    private readonly object snapshotGate = new();
    private Snapshot state;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ConnectionWhitelist(string path)
    {
        this.path = Path.GetFullPath(path);
        lockPath = this.path + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        using FileStream lease = AcquireFileLock(CancellationToken.None);
        try
        {
            state = Load(this.path);
        }
        catch (FileNotFoundException) when (lease.Length == 0)
        {
            // Keep the marker even if creation is interrupted; lost initialized state must not become an open whitelist.
            MarkInitialized(lease);
            state = new(false, new HashSet<string>(StringComparer.Ordinal));
            Save(state, CancellationToken.None, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            // An existing invalid store is not a first-boot store either.
            MarkInitialized(lease);
            throw;
        }
        MarkInitialized(lease);
    }

    // Last valid snapshot, retained if an external edit is invalid or the store cannot be read.
    public bool Enabled => Volatile.Read(ref state).Enabled;
    public IReadOnlyList<string> Addresses => Volatile.Read(ref state).Addresses.Order(StringComparer.Ordinal).ToArray();

    public PacketProcessingDecision Evaluate(ConnectionMetadataSnapshot connection)
    {
        Snapshot current;
        try { current = Refresh(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new(PacketDecisionKind.Reject, "admission.whitelist_unavailable");
        }
        return !current.Enabled || TryAddress(connection.EffectiveEndpoint, out string? address) && Matches(current, address!)
            ? PacketProcessingDecision.Allow : new(PacketDecisionKind.Reject, "admission.whitelist");
    }

    private static bool Matches(Snapshot current, string address)
    {
        if (current.Addresses.Contains(address)) return true;
        if (current.Networks.Length == 0) return false;
        IPAddress parsed = IPAddress.Parse(address);
        foreach (IPNetwork network in current.Networks) if (network.Contains(parsed)) return true;
        return false;
    }

    public ValueTask<bool> SetEnabledAsync(bool enabled, CancellationToken token = default) => ChangeAsync(current =>
        current.Enabled == enabled ? null : current with { Enabled = enabled }, token);

    public async ValueTask<bool> ReloadAsync(CancellationToken token = default)
    {
        await Task.Run(Refresh, token).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<(bool Enabled, IReadOnlyList<string> Addresses)> ReadAsync(CancellationToken token = default)
    {
        Snapshot current = await Task.Run(Refresh, token).ConfigureAwait(false);
        return (current.Enabled, current.Addresses.Order(StringComparer.Ordinal).ToArray());
    }

    public ValueTask<bool> SetAddressAsync(string raw, bool add, CancellationToken token = default)
    {
        if (!TryNormalizeEntry(raw, out string address)) return ValueTask.FromResult(false);
        return ChangeAsync(current =>
        {
            var addresses = new HashSet<string>(current.Addresses, StringComparer.Ordinal);
            bool changed = add ? addresses.Count < MaximumAddresses && addresses.Add(address) : addresses.Remove(address);
            return changed ? CreateSnapshot(current.Enabled, addresses) : null;
        }, token);
    }

    /// <summary>Explicit offline import into a new/empty disabled deployment whitelist.</summary>
    public ValueTask<bool> ImportAsync(IEnumerable<string> entries, bool enabled, CancellationToken token = default)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in entries)
        {
            if (!TryNormalizeEntry(entry, out string value)) throw new InvalidDataException("Invalid imported whitelist IP or network.");
            normalized.Add(value);
            if (normalized.Count > MaximumAddresses) throw new InvalidDataException("Whitelist import exceeds the entry limit.");
        }
        return ChangeAsync(current => current.Enabled || current.Addresses.Count != 0 ? null : CreateSnapshot(enabled, normalized), token);
    }

    private ValueTask<bool> ChangeAsync(Func<Snapshot, Snapshot?> change, CancellationToken token)
    {
        return new ValueTask<bool>(Task.Run(() =>
        {
            using FileStream lease = AcquireFileLock(token);
            // Never derive a write from the potentially stale last-valid snapshot.
            Snapshot? next = change(Refresh());
            if (next is null) return false;
            MarkInitialized(lease);
            Save(next, token);
            // A concurrent local refresh may still be reading the old file; publish after it completes.
            lock (snapshotGate) Volatile.Write(ref state, next);
            return true;
        }, token));
    }

    private Snapshot Refresh()
    {
        lock (snapshotGate)
        {
            Snapshot next = Load(path);
            Volatile.Write(ref state, next);
            return next;
        }
    }

    private FileStream AcquireFileLock(CancellationToken token)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex)
            {
                last = ex;
                if (token.WaitHandle.WaitOne(20)) token.ThrowIfCancellationRequested();
            }
        }
        throw new IOException("Whitelist store is busy or inaccessible.", last);
    }

    private static void MarkInitialized(FileStream lease)
    {
        if (lease.Length != 0) return;
        lease.WriteByte(1);
        lease.Flush(flushToDisk: true);
    }

    private void Save(Snapshot next, CancellationToken token, bool overwrite = true)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, next, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static Snapshot Load(string path)
    {
        // Readers see either complete version of the atomic replacement and do not hold the writer lease.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Whitelist file is too large.");
        var parsed = JsonSerializer.Deserialize<Snapshot>(stream, JsonOptions)
            ?? throw new InvalidDataException("Whitelist file is empty.");
        if (parsed.Addresses is null || parsed.Addresses.Count > MaximumAddresses) throw new InvalidDataException("Invalid whitelist address count.");
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in parsed.Addresses)
        {
            if (!TryNormalizeEntry(value, out string address)) throw new InvalidDataException("Invalid whitelist address or network.");
            addresses.Add(address);
        }
        return CreateSnapshot(parsed.Enabled, addresses);
    }

    private static bool TryAddress(string? endpoint, out string? value)
    {
        if (IPAddress.TryParse(endpoint, out var address)) { value = Normalize(address); return true; }
        if (endpoint is not null && IPEndPoint.TryParse(endpoint, out var parsed)) { value = Normalize(parsed.Address); return true; }
        value = null;
        return false;
    }

    private static string Normalize(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    public static bool TryNormalizeEntry(string raw, out string value)
    {
        if (IPAddress.TryParse(raw, out var address)) { value = Normalize(address); return true; }
        if (IPNetwork.TryParse(raw, out IPNetwork network))
        {
            if (network.BaseAddress.IsIPv4MappedToIPv6 && network.PrefixLength >= 96)
                network = new IPNetwork(network.BaseAddress.MapToIPv4(), network.PrefixLength - 96);
            value = network.ToString();
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static Snapshot CreateSnapshot(bool enabled, HashSet<string> addresses) => new(enabled, addresses)
    {
        Networks = addresses.Where(address => address.Contains('/')).Select(IPNetwork.Parse).ToArray()
    };

    private sealed record Snapshot([property: JsonRequired] bool Enabled, [property: JsonRequired] HashSet<string> Addresses)
    {
        [JsonIgnore] public IPNetwork[] Networks { get; init; } = [];
    }
}

public sealed class WhitelistCommand(ConnectionWhitelist whitelist) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("whitelist", [], ["vega.admin.whitelist"],
        Syntax: "whitelist list [page] | on | off | add <ip|cidr> | del <ip|cidr>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        var args = invocation.Arguments;
        string action = args.Count == 0 ? "list" : args[0].ToLowerInvariant();
        try
        {
            if (action == "list" && args.Count <= 2)
            {
                int page = 1;
                if (args.Count == 2 && (!int.TryParse(args[1], out page) || page < 1)) return Usage();
                var (enabled, entries) = await whitelist.ReadAsync(token).ConfigureAwait(false);
                int pages = Math.Max(1, (entries.Count + 19) / 20);
                if (page > pages) return Usage();
                return CommandExecutionResult.WithMessages(MessageDescriptor.Info("whitelist.list", enabled, page, pages,
                    string.Join(", ", entries.Skip((page - 1) * 20).Take(20))).WithDefaultText("Whitelist enabled={0}; page {1}/{2}: {3}"));
            }
            bool applied;
            if (action is "on" or "off" && args.Count == 1)
                applied = await whitelist.SetEnabledAsync(action == "on", token).ConfigureAwait(false);
            else if (action is "add" or "del" && args.Count == 2 && ConnectionWhitelist.TryNormalizeEntry(args[1], out _))
                applied = await whitelist.SetAddressAsync(args[1], action == "add", token).ConfigureAwait(false);
            else if (args.Count == 1 && ConnectionWhitelist.TryNormalizeEntry(args[0], out _))
                applied = await whitelist.SetAddressAsync(args[0], true, token).ConfigureAwait(false);
            else return Usage();
            return CommandExecutionResult.WithMessages(MessageDescriptor.Info("whitelist.changed", applied)
                .WithDefaultText("Whitelist updated: {0}."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return CommandExecutionResult.Failed("whitelist.storage", MessageDescriptor.Error("whitelist.storage")
                .WithDefaultText("Shared whitelist could not be read or updated."));
        }
    }

    private static CommandExecutionResult Usage() => new(CommandExecutionStatus.InvalidUsage, "whitelist.usage",
        [MessageDescriptor.Error("whitelist.usage").WithDefaultText("Usage: whitelist list [page] | on | off | add <ip|cidr> | del <ip|cidr>.")]);
}
