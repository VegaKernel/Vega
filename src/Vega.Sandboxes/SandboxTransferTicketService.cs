using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxTransferTicketService : ISandboxTransferTicketService
{
    private const int MaximumConsumedTickets = 4096;
    private readonly object sync = new();
    private readonly byte[] key;
    private readonly TimeSpan lifetime;
    private readonly Dictionary<Guid, DateTimeOffset> consumed = [];

    public SandboxTransferTicketService(string keyPath, TimeSpan? lifetime = null)
    {
        key = SandboxKeyFile.LoadOrCreate(keyPath);
        this.lifetime = lifetime ?? TimeSpan.FromSeconds(30);
    }

    public SandboxTransferTicket Issue(string accountId, WorldId originWorldId, WorldId targetWorldId, Guid? sscLeaseId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        DateTimeOffset issued = DateTimeOffset.UtcNow;
        var unsigned = new SandboxTransferTicket(
            Guid.NewGuid(),
            accountId.Trim(),
            originWorldId,
            targetWorldId,
            issued,
            issued + lifetime,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            string.Empty,
            sscLeaseId);
        return unsigned with { Signature = Sign(unsigned) };
    }

    public SandboxTransferValidationStatus ValidateAndConsume(
        SandboxTransferTicket ticket,
        WorldId receivingWorldId,
        DateTimeOffset now)
    {
        if (ticket.TargetWorldId != receivingWorldId)
        {
            return SandboxTransferValidationStatus.WrongTarget;
        }

        if (ticket.ExpiresAt <= now || ticket.IssuedAt > now + TimeSpan.FromSeconds(5))
        {
            return SandboxTransferValidationStatus.Expired;
        }

        byte[] expected = Encoding.UTF8.GetBytes(Sign(ticket with { Signature = string.Empty }));
        byte[] supplied = Encoding.UTF8.GetBytes(ticket.Signature);
        if (expected.Length != supplied.Length || !CryptographicOperations.FixedTimeEquals(expected, supplied))
        {
            return SandboxTransferValidationStatus.InvalidSignature;
        }

        lock (sync)
        {
            foreach (Guid expired in consumed.Where(pair => pair.Value <= now).Select(static pair => pair.Key).ToArray())
            {
                consumed.Remove(expired);
            }

            if (consumed.ContainsKey(ticket.TicketId))
            {
                return SandboxTransferValidationStatus.Replayed;
            }

            if (consumed.Count >= MaximumConsumedTickets)
            {
                Guid oldest = consumed.MinBy(static pair => pair.Value).Key;
                consumed.Remove(oldest);
            }

            consumed.Add(ticket.TicketId, ticket.ExpiresAt);
            return SandboxTransferValidationStatus.Accepted;
        }
    }

    private string Sign(SandboxTransferTicket ticket)
    {
        string canonical = string.Join('|',
            ticket.TicketId.ToString("N"),
            ticket.AccountId,
            ticket.OriginWorldId.Value,
            ticket.TargetWorldId.Value,
            ticket.IssuedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ticket.ExpiresAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ticket.Nonce,
            ticket.SscLeaseId?.ToString("N") ?? string.Empty);
        return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

}

internal static class SandboxKeyFile
{
    public static byte[] LoadOrCreate(string path)
    {
        string resolved = Path.GetFullPath(path);
        if (File.Exists(resolved))
        {
            byte[] existing = Convert.FromBase64String(File.ReadAllText(resolved).Trim());
            if (existing.Length < 32)
            {
                throw new InvalidDataException("Sandbox routing key must contain at least 256 bits.");
            }

            return existing;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
        byte[] created = RandomNumberGenerator.GetBytes(32);
        string temporary = resolved + ".tmp";
        File.WriteAllText(temporary, Convert.ToBase64String(created));
        File.Move(temporary, resolved, false);
        return created;
    }
}
