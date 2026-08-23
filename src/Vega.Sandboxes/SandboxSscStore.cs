using System.Text;
using System.Text.Json;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

/// <summary>
/// Small shared persistence boundary for sandbox SSC state. Every operation reloads under an
/// inter-process file lock so independently supervised Vega backends cannot overwrite each other's
/// revisions with stale in-memory copies.
/// </summary>
public sealed class SandboxSscStore : ISandboxSscStore
{
    private const int MaximumPayloadBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object sync = new();
    private readonly string path;
    private readonly string lockPath;

    public SandboxSscStore(string path)
    {
        this.path = Path.GetFullPath(path);
        lockPath = this.path + ".lock";
    }

    public bool Supports(SandboxSscMode mode) => Enum.IsDefined(mode);

    public SandboxSscStateSnapshot? Read(string accountId, WorldId worldId, SandboxSscMode mode)
    {
        if (mode == SandboxSscMode.Disabled)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return WithLockedState(DateTimeOffset.UtcNow, state =>
            FindState(state, accountId, Scope(worldId, mode))?.ToSnapshot());
    }

    public SandboxSscWriteResult Save(
        string accountId,
        WorldId worldId,
        SandboxSscMode mode,
        string payload,
        long expectedRevision,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(payload);
        if (mode == SandboxSscMode.Disabled)
        {
            return new SandboxSscWriteResult(
                SandboxSscWriteStatus.Rejected,
                ErrorCode: "sandbox.ssc.disabled",
                Message: "SSC is disabled for this world.");
        }

        if (!IsPayloadValid(payload))
        {
            return new SandboxSscWriteResult(
                SandboxSscWriteStatus.Rejected,
                ErrorCode: "sandbox.ssc.payload_too_large",
                Message: $"SSC payload exceeds {MaximumPayloadBytes} bytes.");
        }

        return WithLockedState(now, state =>
        {
            WorldId scope = Scope(worldId, mode);
            Entry? existing = FindState(state, accountId, scope);
            long current = existing?.Revision ?? 0;
            if (current != expectedRevision)
            {
                return new SandboxSscWriteResult(
                    SandboxSscWriteStatus.Conflict,
                    existing?.ToSnapshot(),
                    "sandbox.ssc.revision_conflict",
                    $"Expected SSC revision {expectedRevision}, current revision is {current}.");
            }

            Entry next = WriteEntry(state, accountId, scope, payload, now);
            return new SandboxSscWriteResult(SandboxSscWriteStatus.Applied, next.ToSnapshot());
        });
    }

    public SandboxSscTransferPreparation PrepareTransfer(
        string accountId,
        WorldId originWorldId,
        WorldId targetWorldId,
        SandboxSscMode originMode,
        SandboxSscMode targetMode,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        if (originMode == SandboxSscMode.Disabled && targetMode == SandboxSscMode.Disabled)
        {
            return new SandboxSscTransferPreparation(true);
        }

        if (!Supports(originMode) || !Supports(targetMode) || lifetime <= TimeSpan.Zero)
        {
            return Reject("sandbox.ssc.unsupported", "SSC policy is not supported.");
        }

        return WithLockedState(now, state =>
        {
            WorldId? originScope = originMode == SandboxSscMode.Disabled ? null : Scope(originWorldId, originMode);
            WorldId? targetScope = targetMode == SandboxSscMode.Disabled ? null : Scope(targetWorldId, targetMode);
            long originRevision = originScope is null ? 0 : FindState(state, accountId, originScope.Value)?.Revision ?? 0;
            long targetRevision = targetScope is null ? 0 : FindState(state, accountId, targetScope.Value)?.Revision ?? 0;

            var lease = new Lease
            {
                LeaseId = Guid.NewGuid(),
                AccountId = accountId.Trim(),
                OriginWorldId = originWorldId.Value,
                TargetWorldId = targetWorldId.Value,
                OriginMode = originMode,
                TargetMode = targetMode,
                ExpiresAt = now + lifetime,
                OriginRevision = originRevision,
                TargetRevision = targetRevision
            };
            state.Leases.Add(lease);
            return new SandboxSscTransferPreparation(true, lease.ToContract());
        });
    }

    public SandboxSscTransferPreparation StageTransferState(
        Guid leaseId,
        string accountId,
        string payload,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentNullException.ThrowIfNull(payload);
        if (!IsPayloadValid(payload))
        {
            return Reject("sandbox.ssc.payload_too_large", $"SSC payload exceeds {MaximumPayloadBytes} bytes.");
        }

        return WithLockedState(now, state =>
        {
            Lease? lease = FindLease(state, leaseId, accountId, now);
            if (lease is null)
            {
                return Reject("sandbox.ssc.lease_missing", "SSC transfer lease was not found or expired.");
            }

            if (lease.OriginMode != SandboxSscMode.Disabled)
            {
                WorldId originScope = Scope(new WorldId(lease.OriginWorldId), lease.OriginMode);
                Entry? current = FindState(state, accountId, originScope);
                long revision = current?.Revision ?? 0;
                if (revision != lease.OriginRevision)
                {
                    return Reject(
                        "sandbox.ssc.revision_conflict",
                        $"Origin SSC revision changed from {lease.OriginRevision} to {revision} while transfer was being prepared.");
                }

                Entry saved = WriteEntry(state, accountId, originScope, payload, now);
                lease.OriginRevision = saved.Revision;
                WorldId? targetScope = lease.TargetMode == SandboxSscMode.Disabled
                    ? null
                    : Scope(new WorldId(lease.TargetWorldId), lease.TargetMode);
                if (targetScope is not null && targetScope.Value == originScope)
                {
                    lease.TargetRevision = saved.Revision;
                }
            }

            lease.SourcePayload = payload;
            return new SandboxSscTransferPreparation(true, lease.ToContract());
        });
    }

    public SandboxSscTransferPreparation CommitTransfer(
        Guid leaseId,
        string accountId,
        WorldId receivingWorldId,
        DateTimeOffset now)
    {
        return WithLockedState(now, state =>
        {
            Lease? lease = FindLease(state, leaseId, accountId, now);
            if (lease is null)
            {
                return Reject("sandbox.ssc.lease_missing", "SSC transfer lease was not found or expired.");
            }

            if (!string.Equals(lease.TargetWorldId, receivingWorldId.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Reject("sandbox.ssc.lease_mismatch", "SSC transfer lease does not match this target world.");
            }

            if (lease.TargetMode != SandboxSscMode.Disabled)
            {
                WorldId targetScope = Scope(receivingWorldId, lease.TargetMode);
                Entry? target = FindState(state, accountId, targetScope);
                long currentTargetRevision = target?.Revision ?? 0;
                if (currentTargetRevision != lease.TargetRevision)
                {
                    return Reject(
                        "sandbox.ssc.revision_conflict",
                        $"Target SSC revision changed from {lease.TargetRevision} to {currentTargetRevision} while transfer was in flight.");
                }

                switch (lease.TargetMode)
                {
                    case SandboxSscMode.Inherit:
                        if (lease.OriginMode is SandboxSscMode.CopyOnEnter or SandboxSscMode.Isolated)
                        {
                            if (target is null)
                            {
                                return Reject(
                                    "sandbox.ssc.primary_baseline_missing",
                                    "The shared primary character baseline is missing; refusing to leak sandbox state into primary.");
                            }

                            break;
                        }

                        if (lease.SourcePayload is null)
                        {
                            return Reject("sandbox.ssc.source_state_missing", "Source character state was not staged before transfer.");
                        }

                        WorldId? originScope = lease.OriginMode == SandboxSscMode.Disabled
                            ? null
                            : Scope(new WorldId(lease.OriginWorldId), lease.OriginMode);
                        if (originScope is null || originScope.Value != targetScope)
                        {
                            WriteEntry(state, accountId, targetScope, lease.SourcePayload, now);
                        }
                        break;
                    case SandboxSscMode.CopyOnEnter:
                        if (lease.SourcePayload is null)
                        {
                            return Reject("sandbox.ssc.source_state_missing", "Source character state was not staged before transfer.");
                        }

                        WriteEntry(state, accountId, targetScope, lease.SourcePayload, now);
                        break;
                    case SandboxSscMode.Isolated:
                        if (target is null && lease.SourcePayload is not null)
                        {
                            WriteEntry(state, accountId, targetScope, lease.SourcePayload, now);
                        }
                        break;
                    case SandboxSscMode.Disabled:
                        break;
                    default:
                        return Reject("sandbox.ssc.unsupported", "Target SSC mode is not supported.");
                }
            }

            state.Leases.Remove(lease);
            return new SandboxSscTransferPreparation(true, lease.ToContract());
        });
    }

    public int DeleteWorldState(WorldId worldId)
    {
        if (worldId == WorldId.Primary)
        {
            return 0;
        }

        return WithLockedState(DateTimeOffset.UtcNow, state =>
        {
            int removed = state.Entries.RemoveAll(entry =>
                string.Equals(entry.ScopeWorldId, worldId.Value, StringComparison.OrdinalIgnoreCase));
            state.Leases.RemoveAll(lease =>
                string.Equals(lease.OriginWorldId, worldId.Value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(lease.TargetWorldId, worldId.Value, StringComparison.OrdinalIgnoreCase));
            return removed;
        });
    }

    private T WithLockedState<T>(DateTimeOffset now, Func<State, T> action)
    {
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream fileLock = AcquireFileLock();
            State state = Load(path);
            state.Leases.RemoveAll(lease => lease.ExpiresAt <= now);
            T result = action(state);
            SaveAtomic(state);
            return result;
        }
    }

    private FileStream AcquireFileLock()
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(5);
            }
        }

        throw new IOException("Sandbox SSC store is busy.", last);
    }

    private static Lease? FindLease(State state, Guid leaseId, string accountId, DateTimeOffset now) =>
        state.Leases.FirstOrDefault(candidate =>
            candidate.LeaseId == leaseId &&
            candidate.ExpiresAt > now &&
            string.Equals(candidate.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

    private static WorldId Scope(WorldId worldId, SandboxSscMode mode) => mode == SandboxSscMode.Inherit
        ? WorldId.Primary
        : worldId;

    private static Entry? FindState(State state, string accountId, WorldId scope) => state.Entries.FirstOrDefault(entry =>
        string.Equals(entry.AccountId, accountId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.ScopeWorldId, scope.Value, StringComparison.OrdinalIgnoreCase));

    private static Entry WriteEntry(State state, string accountId, WorldId scope, string payload, DateTimeOffset now)
    {
        long revision = checked(state.Sequence + 1);
        state.Sequence = revision;
        var entry = new Entry(accountId.Trim(), scope.Value, revision, payload, now);
        int index = state.Entries.FindIndex(candidate =>
            string.Equals(candidate.AccountId, entry.AccountId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.ScopeWorldId, entry.ScopeWorldId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            state.Entries.Add(entry);
        }
        else
        {
            state.Entries[index] = entry;
        }

        return entry;
    }

    private static bool IsPayloadValid(string payload) => Encoding.UTF8.GetByteCount(payload) <= MaximumPayloadBytes;

    private static State Load(string path)
    {
        if (!File.Exists(path))
        {
            return new State();
        }

        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOptions) ?? new State();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Sandbox SSC store '{path}' is invalid: {ex.Message}", ex);
        }
    }

    private void SaveAtomic(State state)
    {
        string temporary = path + $".tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static SandboxSscTransferPreparation Reject(string code, string message) =>
        new(false, ErrorCode: code, Message: message);

    public sealed class State
    {
        public long Sequence { get; set; }
        public List<Entry> Entries { get; init; } = [];
        public List<Lease> Leases { get; init; } = [];
    }

    public sealed record Entry(
        string AccountId,
        string ScopeWorldId,
        long Revision,
        string Payload,
        DateTimeOffset UpdatedAt)
    {
        public SandboxSscStateSnapshot ToSnapshot() =>
            new(AccountId, new WorldId(ScopeWorldId), Revision, Payload, UpdatedAt);
    }

    public sealed class Lease
    {
        public Guid LeaseId { get; init; }
        public string AccountId { get; init; } = string.Empty;
        public string OriginWorldId { get; init; } = string.Empty;
        public string TargetWorldId { get; init; } = string.Empty;
        public SandboxSscMode OriginMode { get; init; }
        public SandboxSscMode TargetMode { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public long OriginRevision { get; set; }
        public long TargetRevision { get; set; }
        public string? SourcePayload { get; set; }

        public SandboxSscTransferLease ToContract() => new(
            LeaseId,
            AccountId,
            new WorldId(OriginWorldId),
            new WorldId(TargetWorldId),
            OriginMode,
            TargetMode,
            ExpiresAt);
    }
}
