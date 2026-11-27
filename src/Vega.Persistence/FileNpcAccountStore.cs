using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vega.Contracts.Worlds;

namespace Vega.Persistence;

/// <summary>World-local account ledger. Independent account/scope files are cross-process locked and atomically promoted.</summary>
public sealed class FileNpcAccountStore(string directory) : INpcQuestProgressStore, INpcEconomyProvider
{
    // ponytail: retain operation receipts instead of evicting replay protection. High-volume
    // economies can implement INpcEconomyProvider using indexed transactional storage.
    public const int MaximumOperations = 4096;
    private const int MaximumQuests = 4096;
    private const int MaximumCurrencies = 128;
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private readonly string root = Path.GetFullPath(directory);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<IReadOnlyDictionary<string, NpcQuestProgress>> ReadAsync(NpcAccountScope scope, CancellationToken cancellationToken = default) =>
        Access<IReadOnlyDictionary<string, NpcQuestProgress>>(scope, state =>
            (new ReadOnlyDictionary<string, NpcQuestProgress>(new Dictionary<string, NpcQuestProgress>(state.Quests, StringComparer.Ordinal)), false), cancellationToken);

    public ValueTask<NpcQuestProgressUpdate> AcceptAsync(NpcAccountScope scope, string questId, bool repeatable, CancellationToken cancellationToken = default)
    {
        ValidateName(questId);
        return Access(scope, state =>
        {
            NpcQuestProgress previous = GetQuest(state, questId);
            if (previous.State == NpcQuestState.CompletionPending) return (new NpcQuestProgressUpdate(NpcQuestStatus.RecoveryRequired, previous), false);
            if (previous.State == NpcQuestState.Accepted) return (new NpcQuestProgressUpdate(NpcQuestStatus.AlreadyAccepted, previous), false);
            if (previous.State == NpcQuestState.Completed && !repeatable) return (new NpcQuestProgressUpdate(NpcQuestStatus.AlreadyCompleted, previous), false);
            if (!state.Quests.ContainsKey(questId) && state.Quests.Count >= MaximumQuests) throw new InvalidDataException("Quest ledger capacity reached.");
            var next = new NpcQuestProgress(NpcQuestState.Accepted);
            state.Quests[questId] = next;
            return (new NpcQuestProgressUpdate(NpcQuestStatus.Applied, next), true);
        }, cancellationToken);
    }

    public ValueTask<NpcQuestProgressUpdate> ReserveTurnInAsync(NpcAccountScope scope, string questId, Guid operationId, CancellationToken cancellationToken = default)
    {
        ValidateName(questId);
        ValidateOperation(operationId);
        return Access(scope, state =>
        {
            NpcQuestProgress previous = GetQuest(state, questId);
            if (previous.State == NpcQuestState.CompletionPending)
                return (new NpcQuestProgressUpdate(NpcQuestStatus.RecoveryRequired, previous), false);
            if (previous.State != NpcQuestState.Accepted)
                return (new NpcQuestProgressUpdate(previous.State == NpcQuestState.Completed ? NpcQuestStatus.AlreadyCompleted : NpcQuestStatus.NotAccepted, previous), false);
            // An already released ID cannot be recycled into a second reservation.
            if (previous.OperationId == operationId) return (new NpcQuestProgressUpdate(NpcQuestStatus.RecoveryRequired, previous), false);
            var next = new NpcQuestProgress(NpcQuestState.CompletionPending, operationId);
            state.Quests[questId] = next;
            return (new NpcQuestProgressUpdate(NpcQuestStatus.Applied, next), true);
        }, cancellationToken);
    }

    public ValueTask<bool> FinalizeTurnInAsync(NpcAccountScope scope, string questId, Guid operationId, bool committed, CancellationToken cancellationToken = default)
    {
        ValidateName(questId);
        ValidateOperation(operationId);
        return Access(scope, state =>
        {
            var previous = GetQuest(state, questId);
            var next = committed ? NpcQuestState.Completed : NpcQuestState.Accepted;
            if (previous.OperationId != operationId) return (false, false);
            if (previous.State == next) return (true, false);
            if (previous.State != NpcQuestState.CompletionPending) return (false, false);
            state.Quests[questId] = new(next, operationId);
            return (true, true);
        }, cancellationToken);
    }

    public ValueTask<NpcEconomyBalanceResult> ReadBalanceAsync(NpcAccountScope scope, string currencyId, CancellationToken cancellationToken = default)
    {
        ValidateName(currencyId);
        return Access(scope, state => (Balance(state, currencyId), false), cancellationToken);
    }

    public ValueTask<NpcEconomyBalanceResult> CreditAsync(NpcAccountScope scope, NpcEconomyPrice credit, Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!credit.IsValid || operationId == Guid.Empty) return ValueTask.FromResult(new NpcEconomyBalanceResult(NpcEconomyStatus.InvalidRequest));
        return Access(scope, state =>
        {
            long balance = state.Balances.GetValueOrDefault(credit.CurrencyId);
            if (state.Operations.TryGetValue(operationId, out var previous))
                return (new NpcEconomyBalanceResult(previous is { Kind: WalletKind.Credit, State: WalletState.Committed } &&
                    previous.CurrencyId == credit.CurrencyId && previous.Amount == credit.Amount ? NpcEconomyStatus.AlreadyApplied : NpcEconomyStatus.Conflict, balance), false);
            if (Pending(state) is { } pending) return (new NpcEconomyBalanceResult(NpcEconomyStatus.RecoveryRequired, balance, pending), false);
            if (state.Operations.Count >= MaximumOperations || !state.Balances.ContainsKey(credit.CurrencyId) && state.Balances.Count >= MaximumCurrencies)
                return (new NpcEconomyBalanceResult(NpcEconomyStatus.StorageFull, balance), false);
            if (credit.Amount > NpcEconomyPrice.MaximumBalance - balance)
                return (new NpcEconomyBalanceResult(NpcEconomyStatus.InvalidRequest, balance), false);
            state.Balances[credit.CurrencyId] = balance += credit.Amount;
            state.Operations.Add(operationId, new(credit.CurrencyId, credit.Amount, WalletKind.Credit, WalletState.Committed));
            return (new NpcEconomyBalanceResult(NpcEconomyStatus.Applied, balance), true);
        }, cancellationToken);
    }

    public ValueTask<NpcEconomyReservationResult> ReserveDebitAsync(NpcAccountScope scope, NpcEconomyPrice price, Guid operationId, CancellationToken cancellationToken = default)
    {
        if (!price.IsValid || operationId == Guid.Empty) return ValueTask.FromResult(new NpcEconomyReservationResult(NpcEconomyStatus.InvalidRequest, operationId));
        return Access(scope, state =>
        {
            long balance = state.Balances.GetValueOrDefault(price.CurrencyId);
            if (state.Operations.TryGetValue(operationId, out var previous))
            {
                var status = previous.Kind != WalletKind.Debit || previous.CurrencyId != price.CurrencyId || previous.Amount != price.Amount
                    ? NpcEconomyStatus.Conflict : previous.State switch
                    { WalletState.Pending => NpcEconomyStatus.RecoveryRequired, WalletState.Committed => NpcEconomyStatus.AlreadyApplied, _ => NpcEconomyStatus.Conflict };
                return (new NpcEconomyReservationResult(status, operationId, balance), false);
            }
            if (Pending(state) is { } pending) return (new NpcEconomyReservationResult(NpcEconomyStatus.RecoveryRequired, pending, balance), false);
            if (balance < price.Amount) return (new NpcEconomyReservationResult(NpcEconomyStatus.InsufficientFunds, operationId, balance), false);
            if (state.Operations.Count >= MaximumOperations) return (new NpcEconomyReservationResult(NpcEconomyStatus.StorageFull, operationId, balance), false);
            state.Balances[price.CurrencyId] = balance -= price.Amount;
            state.Operations.Add(operationId, new(price.CurrencyId, price.Amount, WalletKind.Debit, WalletState.Pending));
            return (new NpcEconomyReservationResult(NpcEconomyStatus.Applied, operationId, balance), true);
        }, cancellationToken);
    }

    public ValueTask<bool> FinalizeDebitAsync(NpcAccountScope scope, Guid operationId, bool committed, CancellationToken cancellationToken = default)
    {
        ValidateOperation(operationId);
        return Access(scope, state =>
        {
            if (!state.Operations.TryGetValue(operationId, out var previous) || previous.Kind != WalletKind.Debit) return (false, false);
            var next = committed ? WalletState.Committed : WalletState.Released;
            if (previous.State == next) return (true, false);
            if (previous.State != WalletState.Pending) return (false, false);
            if (!committed)
            {
                long available = state.Balances.GetValueOrDefault(previous.CurrencyId);
                if (previous.Amount > NpcEconomyPrice.MaximumBalance - available) throw new InvalidDataException("Wallet refund exceeds its recorded capacity.");
                state.Balances[previous.CurrencyId] = available + previous.Amount;
            }
            state.Operations[operationId] = previous with { State = next };
            return (true, true);
        }, cancellationToken);
    }

    private static NpcQuestProgress GetQuest(AccountState state, string questId) => state.Quests.GetValueOrDefault(questId) ?? new(NpcQuestState.Available);
    private static Guid? Pending(AccountState state)
    {
        foreach (var pair in state.Operations) if (pair.Value.State == WalletState.Pending) return pair.Key;
        return null;
    }
    private static NpcEconomyBalanceResult Balance(AccountState state, string currencyId)
    {
        var pending = Pending(state);
        return new(pending is null ? NpcEconomyStatus.Applied : NpcEconomyStatus.RecoveryRequired, state.Balances.GetValueOrDefault(currencyId), pending);
    }
    private static void ValidateName(string name) { if (!NpcAccountScope.IsValidName(name)) throw new ArgumentException("Invalid stable NPC account key."); }
    private static void ValidateOperation(Guid operationId) { if (operationId == Guid.Empty) throw new ArgumentException("Operation ID must be non-empty."); }

    private ValueTask<T> Access<T>(NpcAccountScope scope, Func<AccountState, (T Result, bool Save)> action, CancellationToken token)
    {
        if (!scope.IsValid || scope.EconomyProvider is not null) throw new ArgumentException("Invalid built-in NPC account scope.", nameof(scope));
        // Never perform directory, lock or durability work on Terraria's game thread.
        return new(Task.Run(async () =>
        {
            Directory.CreateDirectory(root);
            string canonical = scope.World.Value.Length.ToString(CultureInfo.InvariantCulture) + ":" + scope.World.Value + "|" +
                scope.Scope.Length.ToString(CultureInfo.InvariantCulture) + ":" + scope.Scope + "|" + scope.Account.Value.ToString(CultureInfo.InvariantCulture);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            string path = Path.Combine(root, key + ".json");
            using var lease = await LockAsync(path + ".lock", token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); // after mutation starts, complete the atomic promotion
            AccountState state = Load(path, scope);
            var (result, save) = action(state);
            if (save) Save(path, state);
            return result;
        }, token));
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            { await Task.Delay(10, token).ConfigureAwait(false); }
        }
    }

    private static AccountState Load(string path, NpcAccountScope scope)
    {
        if (!File.Exists(path)) return new() { Scope = scope };
        if (new FileInfo(path).Length > MaximumFileBytes) throw new InvalidDataException("NPC account ledger exceeds its size limit.");
        using var stream = File.OpenRead(path);
        AccountState state = JsonSerializer.Deserialize<AccountState>(stream, JsonOptions) ?? throw new InvalidDataException("NPC account ledger is empty.");
        if (state.Scope != scope || state.Quests is null || state.Balances is null || state.Operations is null ||
            state.Quests.Count > MaximumQuests || state.Balances.Count > MaximumCurrencies || state.Operations.Count > MaximumOperations ||
            state.Quests.Any(pair => !NpcAccountScope.IsValidName(pair.Key) || pair.Value is null || !Enum.IsDefined(pair.Value.State) ||
                pair.Value.State == NpcQuestState.CompletionPending && (pair.Value.OperationId is null || pair.Value.OperationId == Guid.Empty)) ||
            state.Balances.Any(pair => !NpcAccountScope.IsValidName(pair.Key) || pair.Value < 0 || pair.Value > NpcEconomyPrice.MaximumBalance) ||
            state.Operations.Any(pair => pair.Key == Guid.Empty || pair.Value is null || !new NpcEconomyPrice(pair.Value.CurrencyId, pair.Value.Amount).IsValid ||
                !Enum.IsDefined(pair.Value.Kind) || !Enum.IsDefined(pair.Value.State) || pair.Value.Kind == WalletKind.Credit && pair.Value.State != WalletState.Committed) ||
            state.Operations.Count(pair => pair.Value.State == WalletState.Pending) > 1)
            throw new InvalidDataException("NPC account ledger is inconsistent; it was not overwritten.");
        return state;
    }

    private static void Save(string path, AccountState state)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, JsonOptions);
                if (stream.Length > MaximumFileBytes) throw new InvalidDataException("NPC account ledger exceeds its size limit.");
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private enum WalletKind { Credit, Debit }
    private enum WalletState { Pending, Committed, Released }
    private sealed record WalletOperation(string CurrencyId, long Amount, WalletKind Kind, WalletState State);
    private sealed class AccountState
    {
        public NpcAccountScope Scope { get; init; }
        public Dictionary<string, NpcQuestProgress> Quests { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Balances { get; init; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, WalletOperation> Operations { get; init; } = [];
    }
}
