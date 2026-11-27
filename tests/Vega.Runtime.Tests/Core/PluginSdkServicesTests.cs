using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Operations;
using Vega.Contracts.Accounts;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Modules;
using Vega.Persistence.Sqlite;
using Vega.Persistence;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class PluginSdkServicesTests
{
    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)JsonSerializerOptions.Default.GetTypeInfo(typeof(T));

    [Test]
    public async Task TypedServiceUsesWireContractsAndResolvesTheCurrentActivation()
    {
        var registry = new PluginServiceRegistry();
        await using var consumer = Scope("consumer", registry);
        var address = new PluginServiceAddress("Provider", "balance", 1);
        await using (var provider = Scope("provider", registry))
        {
            provider.Services.Register("balance", 1, TypeInfo<ProviderRequest>(), TypeInfo<ProviderResponse>(),
                (request, _) => ValueTask.FromResult(new ProviderResponse(request.Account + 10)));
            Assert.That(consumer.Services.Snapshot(), Has.Count.EqualTo(1));
            Assert.Throws<InvalidOperationException>(() => provider.Services.Register("Balance", 1,
                TypeInfo<int>(), TypeInfo<int>(), (value, _) => ValueTask.FromResult(value)));
            var result = await consumer.Services.CallAsync(address, new ConsumerRequest(42), TypeInfo<ConsumerRequest>(), TypeInfo<ConsumerResponse>());
            Assert.That(result.Status, Is.EqualTo(PluginServiceStatus.Success));
            Assert.That(result.Value?.Balance, Is.EqualTo(52));
            Assert.That((await consumer.Services.CallAsync(address with { Version = 2 }, 42, TypeInfo<int>(), TypeInfo<int>())).Status,
                Is.EqualTo(PluginServiceStatus.Unavailable));
        }

        Assert.That(consumer.Services.Snapshot(), Is.Empty);
        Assert.That((await consumer.Services.CallAsync(address, 42, TypeInfo<int>(), TypeInfo<int>())).Status, Is.EqualTo(PluginServiceStatus.Unavailable));
        await using var replacement = Scope("provider", registry);
        replacement.Services.Register("balance", 1, TypeInfo<int>(), TypeInfo<int>(), (value, _) => ValueTask.FromResult(value + 100));
        Assert.That((await consumer.Services.CallAsync(address, 42, TypeInfo<int>(), TypeInfo<int>())).Value, Is.EqualTo(142));
    }

    [Test]
    public async Task RetirementCancelsAndDrainsServiceHandlersAndProviderFaultsStayIsolated()
    {
        var registry = new PluginServiceRegistry();
        await using var consumer = Scope("consumer", registry);
        await using var provider = Scope("provider", registry);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Services.Register("fault", 1, TypeInfo<int>(), TypeInfo<int>(), (_, _) => throw new InvalidOperationException("fixture"));
        Assert.That((await consumer.Services.CallAsync(new("provider", "fault", 1), 0, TypeInfo<int>(), TypeInfo<int>())).Status,
            Is.EqualTo(PluginServiceStatus.Failed));
        Assert.That(provider.Faults.Count, Is.EqualTo(1));
        Assert.That(consumer.Faults.Count, Is.Zero);
        provider.Services.Register("wait", 1, TypeInfo<int>(), TypeInfo<int>(), async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        });
        Task<PluginServiceResult<int>> pending = consumer.Services.CallAsync(new("provider", "wait", 1), 0, TypeInfo<int>(), TypeInfo<int>()).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.DrainForRetirementAsync(timeout.Token);
        Assert.That((await pending.WaitAsync(timeout.Token)).Status, Is.EqualTo(PluginServiceStatus.Unavailable));
        Assert.That(consumer.Services.Snapshot(), Is.Empty);
    }

    [Test]
    public async Task DurableDocumentsAreOwnerScopedAndBatchCasSurvivesReopen()
    {
        string path = Path.Combine(Path.GetTempPath(), "vega-plugin-documents-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new SqliteVegaDatabaseOptions(Path.Combine(path, "vega.sqlite3"));
            var first = await SqliteVegaDatabase.OpenAsync(options);
            var registry = new PluginServiceRegistry();
            await using (var owner = Scope("Wallet", registry, first.PluginData))
            {
                await using var other = Scope("Other", registry, first.PluginData);
                Assert.That(await owner.Data.CommitAsync([PluginDataExtensions.Set("Wallets", "Alice", 0, 100, TypeInfo<int>()),
                    PluginDataExtensions.Set("wallets", "bob", 0, 20, TypeInfo<int>())]), Is.True);
                Assert.That(await other.Data.ReadAsync("wallets", "alice"), Is.Null);
                Assert.That(await other.Data.CommitAsync([PluginDataExtensions.Set("wallets", "alice", 0, 7, TypeInfo<int>())]), Is.True);
                var alice = (await owner.Data.ReadAsync("wallets", "alice", TypeInfo<int>()))!;
                var bob = (await owner.Data.ReadAsync("wallets", "bob", TypeInfo<int>()))!;
                Assert.That(alice.Value, Is.EqualTo(100));
                Assert.That(await owner.Data.CommitAsync([PluginDataExtensions.Set("wallets", "alice", alice.Revision, 90, TypeInfo<int>()),
                    PluginDataExtensions.Set("wallets", "bob", bob.Revision + 1, 30, TypeInfo<int>())]), Is.False);
                Assert.That((await owner.Data.ReadAsync("wallets", "alice", TypeInfo<int>()))!.Value, Is.EqualTo(100));
                Assert.That(await owner.Data.CommitAsync([PluginDataExtensions.Set("wallets", "alice", alice.Revision, 90, TypeInfo<int>()),
                    PluginDataExtensions.Set("wallets", "bob", bob.Revision, 30, TypeInfo<int>())]), Is.True);
                Assert.That((await owner.Data.ListAsync("wallets", limit: 1)).Single().Key, Is.EqualTo("alice"));
                Assert.That((await owner.Data.ListAsync("wallets", afterKey: "alice")).Single().Key, Is.EqualTo("bob"));
            }

            var reopened = await SqliteVegaDatabase.OpenAsync(options);
            await using var reloaded = Scope("wallet", registry, reopened.PluginData);
            var current = (await reloaded.Data.ReadAsync("wallets", "alice", TypeInfo<int>()))!;
            Assert.That(current.Value, Is.EqualTo(90));
            Assert.That(await reloaded.Data.CommitAsync([PluginDataExtensions.Delete("wallets", "alice", current.Revision)]), Is.True);
            Assert.That(await reloaded.Data.ReadAsync("wallets", "alice"), Is.Null);
            Assert.That(await reloaded.Data.CommitAsync([PluginDataExtensions.Set("wallets", "alice", 0, 100, TypeInfo<int>())]), Is.True);
            Assert.That(await reloaded.Data.CommitAsync([PluginDataExtensions.Delete("wallets", "alice", current.Revision)]), Is.False,
                "An old CAS revision cannot delete the newly created record.");
            Assert.That(await reloaded.Data.ReadAsync("wallets", "alice", TypeInfo<int>()), Is.Not.Null);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static HotModuleRegistrationScope Scope(string name, PluginServiceRegistry registry,
        Vega.Contracts.Modules.IPluginDocumentStore? documents = null) => new(new CommandDispatcher([]), new Logger(), name,
        Path.GetTempPath(), serviceRegistry: registry, documentStore: documents);

    [Test]
    public async Task NamedNpcEconomyRetirementNeverFallsBackAndPendingReservationSurvivesReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-named-npc-wallet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builtin = new FileNpcAccountStore(Path.Combine(root, "builtin"));
            string externalPath = Path.Combine(root, "external");
            var router = new PluginNpcEconomyRegistry(builtin);
            var scope = new NpcAccountScope(WorldId.Primary, "merchant.wallet", new AccountId(5));
            var address = new NpcEconomyProviderAddress("WalletService", "coins", 1);
            var named = scope with { EconomyProvider = address };
            var grant = new NpcEconomyPrice("coins", 100);
            Guid operationId = Guid.NewGuid();
            await router.CreditAsync(scope, grant, Guid.NewGuid());
            Assert.That((await router.ReserveDebitAsync(named, grant, operationId)).Status, Is.EqualTo(NpcEconomyStatus.Unavailable));
            await using (var plugin = new HotModuleRegistrationScope(new CommandDispatcher([]), new Logger(), address.Plugin, root, npcEconomyRegistry: router))
            {
                plugin.NpcEconomy.Register(address.Name, address.Version, new FileNpcAccountStore(externalPath));
                Assert.That(plugin.NpcEconomy.Snapshot().Single(), Is.EqualTo(address));
                await router.CreditAsync(named, grant, Guid.NewGuid());
                Assert.That((await router.ReserveDebitAsync(named, new("coins", 60), operationId)).Status, Is.EqualTo(NpcEconomyStatus.Applied));
            }
            Assert.That(await router.FinalizeDebitAsync(named, operationId, true), Is.False);
            Assert.That((await router.ReadBalanceAsync(scope, "coins")).Balance, Is.EqualTo(100));
            await using (var replacement = new HotModuleRegistrationScope(new CommandDispatcher([]), new Logger(), address.Plugin, root, npcEconomyRegistry: router))
            {
                replacement.NpcEconomy.Register(address.Name, address.Version, new FileNpcAccountStore(externalPath));
                Assert.That((await router.ReserveDebitAsync(named, new("coins", 60), operationId)).Status, Is.EqualTo(NpcEconomyStatus.RecoveryRequired));
                Assert.That(await router.FinalizeDebitAsync(named, operationId, true), Is.True);
                Assert.That((await router.ReserveDebitAsync(named, new("coins", 60), operationId)).Status, Is.EqualTo(NpcEconomyStatus.AlreadyApplied));
                Assert.That((await router.ReadBalanceAsync(named, "coins")).Balance, Is.EqualTo(40));
                Assert.That((await router.ReadBalanceAsync(named with { EconomyProvider = address with { Version = 2 } }, "coins")).Status, Is.EqualTo(NpcEconomyStatus.Unavailable));
            }
            Assert.That((await router.ReadBalanceAsync(scope, "coins")).Balance, Is.EqualTo(100));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    public sealed record ProviderRequest(int Account);
    public sealed record ConsumerRequest(int Account);
    public sealed record ProviderResponse(int Balance);
    public sealed record ConsumerResponse(int Balance);

    private sealed class Logger : IOperationsLogger
    {
        public void Log(OperationsLogLevel level, string source, string text, Exception? exception = null, string? correlationId = null) { }
    }
}
