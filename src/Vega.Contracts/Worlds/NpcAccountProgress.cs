using Vega.Contracts.Accounts;

namespace Vega.Contracts.Worlds;

/// <summary>Stable world-local, plugin-defined named scope for one authenticated account.</summary>
public readonly record struct NpcAccountScope(WorldId World, string Scope, AccountId Account)
{
    public NpcEconomyProviderAddress? EconomyProvider { get; init; }
    public bool IsValid => !string.IsNullOrWhiteSpace(World.Value) && Account.Value > 0 && IsValidName(Scope) &&
        (EconomyProvider is null || EconomyProvider.IsValid);
    public static bool IsValidName(string? value) => NpcDialogueDefinition.ValidId(value);
}

/// <summary>Stable, explicit named provider identity; an absent address selects Vega's built-in ledger.</summary>
public sealed record NpcEconomyProviderAddress(string Plugin, string Name, int Version)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Plugin) && Plugin.Length <= 255 &&
        !string.IsNullOrWhiteSpace(Name) && Name.Length <= 96 && Version > 0 &&
        Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}

public sealed record NpcQuestProgress(NpcQuestState State, Guid? OperationId = null);
public sealed record NpcQuestProgressUpdate(NpcQuestStatus Status, NpcQuestProgress Progress);

/// <summary>Durable quest transitions. A reserved turn-in must survive process death until explicitly resolved.</summary>
public interface INpcQuestProgressStore
{
    /// <summary>Return a detached immutable snapshot of durable progress in this scope.</summary>
    ValueTask<IReadOnlyDictionary<string, NpcQuestProgress>> ReadAsync(NpcAccountScope scope, CancellationToken cancellationToken = default);
    ValueTask<NpcQuestProgressUpdate> AcceptAsync(NpcAccountScope scope, string questId, bool repeatable, CancellationToken cancellationToken = default);
    /// <summary>Only a newly reserved operation returns Applied. Existing pending IDs return RecoveryRequired, not another grant authorization.</summary>
    ValueTask<NpcQuestProgressUpdate> ReserveTurnInAsync(NpcAccountScope scope, string questId, Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>Idempotently finalize a matching reservation; false releases it back to Accepted.</summary>
    ValueTask<bool> FinalizeTurnInAsync(NpcAccountScope scope, string questId, Guid operationId, bool committed, CancellationToken cancellationToken = default);
}

public readonly record struct NpcEconomyPrice(string CurrencyId, long Amount)
{
    public const long MaximumBalance = 1_000_000_000_000;
    public bool IsValid => NpcDialogueDefinition.ValidId(CurrencyId) && Amount is > 0 and <= MaximumBalance;
}

public enum NpcEconomyStatus { Applied, AlreadyApplied, Unavailable, AccountRequired, InvalidRequest, InsufficientFunds,
    RecoveryRequired, Conflict, StorageFull }
public sealed record NpcEconomyBalanceResult(NpcEconomyStatus Status, long Balance = 0, Guid? PendingOperationId = null);
public sealed record NpcEconomyReservationResult(NpcEconomyStatus Status, Guid OperationId, long RemainingBalance = 0);

/// <summary>
/// Pluggable virtual economy provider. Reservation durably owns funds before inventory grant;
/// operations are idempotent by caller-provided ID. A pending debit must not expire or refund
/// automatically, because native inventory may have committed before a process failure.
/// Providers execute off the authoritative game thread.
/// </summary>
public interface INpcEconomyProvider
{
    /// <summary>Whether this host provider can route explicitly addressed plugin providers.</summary>
    bool SupportsNamedProviders => false;
    ValueTask<NpcEconomyBalanceResult> ReadBalanceAsync(NpcAccountScope scope, string currencyId, CancellationToken cancellationToken = default);
    ValueTask<NpcEconomyBalanceResult> CreditAsync(NpcAccountScope scope, NpcEconomyPrice credit, Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>Only a new debit reservation returns Applied. An existing pending ID returns RecoveryRequired; a committed ID returns AlreadyApplied.</summary>
    ValueTask<NpcEconomyReservationResult> ReserveDebitAsync(NpcAccountScope scope, NpcEconomyPrice price, Guid operationId, CancellationToken cancellationToken = default);
    /// <summary>Idempotently commit a debit or refund it on a known native failure; never refund an already committed debit.</summary>
    ValueTask<bool> FinalizeDebitAsync(NpcAccountScope scope, Guid operationId, bool committed, CancellationToken cancellationToken = default);
}
