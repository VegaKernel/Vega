using NUnit.Framework;
using Vega.Persistence;

namespace Vega.Runtime.Tests.Persistence;

[TestFixture]
public sealed class ExternalVegaDatabaseContractTests
{
    [TestCase("postgresql", "VEGA_TEST_POSTGRESQL")]
    [TestCase("mysql", "VEGA_TEST_MYSQL")]
    public async Task EmptyProviderInitializesOneBaselineReopensAndCreatesLogicalDump(
        string provider,
        string environmentVariable)
    {
        string? connectionString = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Ignore($"Set {environmentVariable} to run the disposable {provider} contract test.");
        }

        var options = VegaDatabaseOptions.Default with
        {
            Provider = provider,
            ConnectionString = connectionString
        };
        IVegaDatabase first = VegaDatabaseFactory.Create(options, TestContext.CurrentContext.WorkDirectory);
        await first.InitializeAsync();
        var created = await first.Authorization.CreateGroupAsync("builders", "default", "contract-test");

        IVegaDatabase reopened = VegaDatabaseFactory.Create(options, TestContext.CurrentContext.WorkDirectory);
        await reopened.InitializeAsync();
        string directory = Path.Combine(Path.GetTempPath(), "vega-external-dump-tests", Guid.NewGuid().ToString("N"));
        string dump = Path.Combine(directory, reopened.BackupFileName);
        try
        {
            await reopened.WriteBackupAsync(dump);
            Assert.Multiple(() =>
            {
                Assert.That(first.SchemaVersion, Is.EqualTo(1));
                Assert.That(reopened.SchemaVersion, Is.EqualTo(1));
                Assert.That(created.Succeeded, Is.True);
                Assert.That(reopened.Authorization.SnapshotGroups().Any(group => group.Name == "builders"), Is.True);
                Assert.That(new FileInfo(dump).Length, Is.GreaterThan(0));
            });
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
