using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Worlds;
using Vega.Persistence;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class NpcAccountStoreTests
{
    [Test]
    public async Task QuestReservationsSurviveReopenAndSerializeConcurrentTurnIns()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-npc-quests", Guid.NewGuid().ToString("N"));
        try
        {
            var key = new NpcAccountScope(new("arena"), "plugin.merchant", new AccountId(1));
            var first = new FileNpcAccountStore(root);
            var second = new FileNpcAccountStore(root);
            Assert.That((await first.AcceptAsync(key, "quest", false)).Status, Is.EqualTo(NpcQuestStatus.Applied));
            Assert.That((await second.ReadAsync(key))["quest"].State, Is.EqualTo(NpcQuestState.Accepted));
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            var results = await Task.WhenAll(first.ReserveTurnInAsync(key, "quest", a).AsTask(), second.ReserveTurnInAsync(key, "quest", b).AsTask());
            Assert.That(results.Count(r => r.Status == NpcQuestStatus.Applied), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Status == NpcQuestStatus.RecoveryRequired), Is.EqualTo(1));
            var reopened = new FileNpcAccountStore(root);
            var pending = (await reopened.ReadAsync(key))["quest"];
            Assert.That(pending.State, Is.EqualTo(NpcQuestState.CompletionPending));
            Guid id = pending.OperationId!.Value;
            Assert.That((await reopened.ReserveTurnInAsync(key, "quest", id)).Status, Is.EqualTo(NpcQuestStatus.RecoveryRequired));
            Assert.That(await reopened.FinalizeTurnInAsync(key, "quest", Guid.NewGuid(), false), Is.False);
            Assert.That((await reopened.AcceptAsync(key, "quest", true)).Status, Is.EqualTo(NpcQuestStatus.RecoveryRequired));
            Assert.That(await reopened.FinalizeTurnInAsync(key, "quest", id, false), Is.True);
            Assert.That(await reopened.FinalizeTurnInAsync(key, "quest", id, false), Is.True);
            Assert.That((await reopened.ReserveTurnInAsync(key, "quest", id)).Status, Is.EqualTo(NpcQuestStatus.RecoveryRequired));
            Guid final = Guid.NewGuid();
            Assert.That((await first.ReserveTurnInAsync(key, "quest", final)).Status, Is.EqualTo(NpcQuestStatus.Applied));
            Assert.That(await second.FinalizeTurnInAsync(key, "quest", final, true), Is.True);
            Assert.That(await reopened.FinalizeTurnInAsync(key, "quest", final, true), Is.True);
            Assert.That(await reopened.FinalizeTurnInAsync(key, "quest", final, false), Is.False);
            Assert.That((await reopened.AcceptAsync(key, "quest", false)).Status, Is.EqualTo(NpcQuestStatus.AlreadyCompleted));
            Assert.That(await reopened.ReadAsync(key with { Account = new AccountId(2) }), Is.Empty);
            Assert.That(await reopened.ReadAsync(key with { World = new("another") }), Is.Empty);
            Assert.That(await reopened.ReadAsync(key with { Scope = "other" }), Is.Empty);
            Assert.That((await reopened.AcceptAsync(key, "quest", true)).Progress.State, Is.EqualTo(NpcQuestState.Accepted));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task WalletCreditsAndReservationsAreIdempotentAndFailClosedAfterReopen()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-npc-wallet", Guid.NewGuid().ToString("N"));
        try
        {
            var key = new NpcAccountScope(WorldId.Primary, "plugin.tokens", new AccountId(8));
            var first = new FileNpcAccountStore(root);
            var second = new FileNpcAccountStore(root);
            Guid credit = Guid.NewGuid();
            var grant = new NpcEconomyPrice("tokens", 100);
            Assert.That((await first.CreditAsync(key, grant, credit)).Balance, Is.EqualTo(100));
            Assert.That((await second.CreditAsync(key, grant, credit)).Status, Is.EqualTo(NpcEconomyStatus.AlreadyApplied));
            Assert.That((await first.CreditAsync(key, grant with { Amount = 101 }, credit)).Status, Is.EqualTo(NpcEconomyStatus.Conflict));
            var price = new NpcEconomyPrice("tokens", 60);
            var requests = await Task.WhenAll(first.ReserveDebitAsync(key, price, Guid.NewGuid()).AsTask(), second.ReserveDebitAsync(key, price, Guid.NewGuid()).AsTask());
            var applied = requests.Single(r => r.Status == NpcEconomyStatus.Applied);
            Assert.That(requests.Single(r => r.Status == NpcEconomyStatus.RecoveryRequired).OperationId, Is.EqualTo(applied.OperationId));
            var reopened = new FileNpcAccountStore(root);
            var balance = await reopened.ReadBalanceAsync(key, "tokens");
            Assert.That(balance.Balance, Is.EqualTo(40));
            Assert.That(balance.PendingOperationId, Is.EqualTo(applied.OperationId));
            Assert.That((await reopened.ReserveDebitAsync(key, price, applied.OperationId)).Status, Is.EqualTo(NpcEconomyStatus.RecoveryRequired));
            Assert.That((await reopened.CreditAsync(key, grant, Guid.NewGuid())).Status, Is.EqualTo(NpcEconomyStatus.RecoveryRequired));
            Assert.That(await reopened.FinalizeDebitAsync(key, applied.OperationId, false), Is.True);
            Assert.That(await reopened.FinalizeDebitAsync(key, applied.OperationId, false), Is.True);
            Assert.That((await first.ReadBalanceAsync(key, "tokens")).Balance, Is.EqualTo(100));
            Guid id = Guid.NewGuid();
            Assert.That((await first.ReserveDebitAsync(key, price, id)).Status, Is.EqualTo(NpcEconomyStatus.Applied));
            Assert.That(await second.FinalizeDebitAsync(key, id, true), Is.True);
            Assert.That(await reopened.FinalizeDebitAsync(key, id, true), Is.True);
            Assert.That(await reopened.FinalizeDebitAsync(key, id, false), Is.False);
            Assert.That((await reopened.ReserveDebitAsync(key, price, id)).Status, Is.EqualTo(NpcEconomyStatus.AlreadyApplied));
            Assert.That((await reopened.ReadBalanceAsync(key, "tokens")).Balance, Is.EqualTo(40));
            Assert.That((await reopened.ReadBalanceAsync(key with { Account = new AccountId(9) }, "tokens")).Balance, Is.Zero);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
