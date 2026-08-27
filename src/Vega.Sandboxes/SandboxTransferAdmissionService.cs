using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

/// <summary>
/// Shared, short-lived rendezvous used when Dimensions switches a client between Vega processes.
/// Dimensions' manual-switch packet only carries host and port, so admission is correlated by the
/// trusted forwarded IPv4 client address and canonical client UUID instead of being exposed to the client.
/// </summary>
public sealed class SandboxTransferAdmissionService : ISandboxTransferAdmissionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object sync = new();
    private readonly string path;
    private readonly string lockPath;
    private readonly ISandboxTransferTicketService tickets;
    private readonly ISandboxSscStore? sscStore;

    public SandboxTransferAdmissionService(
        string path,
        ISandboxTransferTicketService tickets,
        ISandboxSscStore? sscStore = null)
    {
        this.path = Path.GetFullPath(path);
        lockPath = this.path + ".lock";
        this.tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        this.sscStore = sscStore;
    }

    public SandboxTransferAdmissionResult Register(
        SandboxTransferTicket ticket,
        string effectiveEndpoint,
        string clientUuid,
        DateTimeOffset now)
    {
        if (ticket.ExpiresAt <= now)
        {
            return Reject("sandbox.transfer.expired", "Transfer ticket is already expired.");
        }

        if (!TryNormalizeIpv4Endpoint(effectiveEndpoint, out string endpoint) ||
            !Guid.TryParseExact(clientUuid, "D", out Guid uuid))
        {
            return Reject("sandbox.transfer.identity_invalid", "Transfer identity is incomplete or invalid.");
        }

        return WithLockedState(state =>
        {
            Prune(state, now);
            state.Admissions.RemoveAll(candidate => candidate.Ticket.TicketId == ticket.TicketId);
            state.Admissions.Add(new PersistedAdmission(
                ticket,
                endpoint,
                uuid.ToString("D"),
                now));
            return new SandboxTransferAdmissionResult(true);
        });
    }

    public SandboxTransferAdmissionResult Consume(
        WorldId receivingWorldId,
        string effectiveEndpoint,
        string clientUuid,
        DateTimeOffset now)
    {
        if (!TryNormalizeIpv4Endpoint(effectiveEndpoint, out string endpoint) ||
            !Guid.TryParseExact(clientUuid, "D", out Guid uuid))
        {
            return Reject("sandbox.transfer.identity_invalid", "Transfer identity is incomplete or invalid.");
        }

        PersistedAdmission? admission = null;
        SandboxTransferAdmissionResult lookup = WithLockedState(state =>
        {
            Prune(state, now);
            int index = state.Admissions.FindIndex(candidate =>
                candidate.Ticket.TargetWorldId == receivingWorldId &&
                string.Equals(candidate.EffectiveEndpoint, endpoint, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.ClientUuid, uuid.ToString("D"), StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return Reject("sandbox.transfer.admission_required", "No matching sandbox transfer admission exists.");
            }

            admission = state.Admissions[index];
            state.Admissions.RemoveAt(index);
            return new SandboxTransferAdmissionResult(true);
        });
        if (!lookup.Accepted || admission is null)
        {
            return lookup;
        }

        SandboxTransferValidationStatus validation = tickets.ValidateAndConsume(
            admission.Ticket,
            receivingWorldId,
            now);
        if (validation != SandboxTransferValidationStatus.Accepted)
        {
            return Reject(
                $"sandbox.transfer.{validation.ToString().ToLowerInvariant()}",
                $"Transfer admission ticket was rejected: {validation}.");
        }

        if (admission.Ticket.SscLeaseId is Guid leaseId)
        {
            if (sscStore is null)
            {
                return Reject("sandbox.ssc.store_unavailable", "SSC persistence is unavailable on the receiving process.");
            }

            SandboxSscTransferPreparation committed = sscStore.CommitTransfer(
                leaseId,
                admission.Ticket.AccountId,
                receivingWorldId,
                now);
            if (!committed.Accepted)
            {
                return Reject(
                    committed.ErrorCode ?? "sandbox.ssc.commit_failed",
                    committed.Message ?? "SSC transfer commit failed.");
            }
        }

        return new SandboxTransferAdmissionResult(true, Ticket: admission.Ticket);
    }

    private SandboxTransferAdmissionResult WithLockedState(Func<PersistedState, SandboxTransferAdmissionResult> action)
    {
        lock (sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream lease = AcquireFileLock();
            PersistedState state = Load();
            SandboxTransferAdmissionResult result = action(state);
            Save(state);
            return result;
        }
    }

    private FileStream AcquireFileLock()
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 40; attempt++)
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

        throw new IOException("Sandbox transfer admission store is busy.", last);
    }

    private PersistedState Load()
    {
        if (!File.Exists(path)) return new PersistedState();

        try
        {
            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), JsonOptions)
                ?? new PersistedState();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Sandbox transfer admission store '{path}' is invalid: {ex.Message}", ex);
        }
    }

    private void Save(PersistedState state)
    {
        string temporary = path + ".tmp." + Environment.ProcessId;
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, true);
    }

    private static void Prune(PersistedState state, DateTimeOffset now) =>
        state.Admissions.RemoveAll(candidate => candidate.Ticket.ExpiresAt <= now);

    private static bool TryNormalizeIpv4Endpoint(string value, out string endpoint)
    {
        endpoint = string.Empty;
        if (IPAddress.TryParse(value, out IPAddress? address) &&
            address.AddressFamily == AddressFamily.InterNetwork)
        {
            endpoint = address.ToString();
            return true;
        }

        if (IPEndPoint.TryParse(value, out IPEndPoint? parsed) &&
            parsed.Address.AddressFamily == AddressFamily.InterNetwork)
        {
            endpoint = parsed.Address.ToString();
            return true;
        }

        return false;
    }

    private static SandboxTransferAdmissionResult Reject(string code, string message) =>
        new(false, code, message);

    public sealed class PersistedState
    {
        public List<PersistedAdmission> Admissions { get; init; } = [];
    }

    public sealed record PersistedAdmission(
        SandboxTransferTicket Ticket,
        string EffectiveEndpoint,
        string ClientUuid,
        DateTimeOffset RegisteredAt);
}
