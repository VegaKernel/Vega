using System.Text.Json.Nodes;
using NUnit.Framework;
using Vega.Contracts.Commands;
using Vega.Contracts.Operations;
using Vega.Core.Modules;
using Vega.PluginSdk;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ManagedConfigurationAdministrationTests
{
    [Test]
    public async Task ExposedManagedConfigRedactsSecretsAndPreservesThemOnSave()
    {
        string pluginName = $"Vega.Test.AdminConfig.{Guid.NewGuid():N}";
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "vega-admin-config-tests",
            Guid.NewGuid().ToString("N"));
        var scope = new HotModuleRegistrationScope(
            new NoopCommandRegistry(),
            new NoopLogger(),
            pluginName,
            dataDirectory);
        string configurationDirectory = scope.Configuration.Directory;

        try
        {
            IPluginConfig<ProbeSettings> config = scope.Configuration.Use(
                "settings.json",
                () => new ProbeSettings("initial", "server-secret"),
                static value => string.IsNullOrWhiteSpace(value.Name) ? "name is required" : null);
            var administration = new ManagedConfigurationAdministration(
            [
                new ManagedConfigurationExposureRule(
                    pluginName,
                    "settings.json",
                    RedactedJsonPointers: ["/secret"],
                    DisplayName: "Probe settings")
            ]);

            ManagedConfigurationAdminDescriptor descriptor = administration.Snapshot().Single();
            ManagedConfigurationAdminResult read = administration.Read(pluginName, "settings.json");
            JsonObject edited = JsonNode.Parse(read.Document!.Json)!.AsObject();
            edited["name"] = "changed";

            ManagedConfigurationAdminResult saved = administration.Replace(
                pluginName,
                "settings.json",
                descriptor.Revision,
                edited.ToJsonString());
            ManagedConfigurationAdminResult stale = administration.Replace(
                pluginName,
                "settings.json",
                descriptor.Revision,
                saved.Document!.Json);

            Assert.Multiple(() =>
            {
                Assert.That(descriptor.HasRedactions, Is.True);
                Assert.That(read.Succeeded, Is.True);
                Assert.That(read.Document!.Json, Does.Contain("[REDACTED]"));
                Assert.That(read.Document.Json, Does.Not.Contain("server-secret"));
                Assert.That(saved.Succeeded, Is.True);
                Assert.That(saved.Document!.Descriptor.Revision, Is.Not.EqualTo(descriptor.Revision));
                Assert.That(config.Current.Name, Is.EqualTo("changed"));
                Assert.That(config.Current.Secret, Is.EqualTo("server-secret"));
                Assert.That(stale.Succeeded, Is.False);
                Assert.That(stale.ErrorCode, Is.EqualTo("configuration.revision_conflict"));
            });
        }
        finally
        {
            await scope.DisposeAsync();
            if (Directory.Exists(configurationDirectory))
            {
                Directory.Delete(configurationDirectory, recursive: true);
            }

            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Test]
    public void InvalidExposurePolicyFailsClosed()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "vega-admin-config-policy-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "admin-configuration.json");
        File.WriteAllText(path, "{ this is not json");

        try
        {
            ManagedConfigurationAdministration administration =
                ManagedConfigurationAdministration.LoadPolicy(path, out string? error);

            Assert.Multiple(() =>
            {
                Assert.That(error, Is.Not.Null.And.Not.Empty);
                Assert.That(administration.Snapshot(), Is.Empty);
                Assert.That(
                    administration.Read("anything", "settings.json").ErrorCode,
                    Is.EqualTo("configuration.not_exposed"));
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record ProbeSettings(string Name, string Secret);

    private sealed class NoopCommandRegistry : ICommandRegistry
    {
        public IDisposable Register(ICommandHandler handler) => NoopDisposable.Instance;
    }

    private sealed class NoopLogger : IOperationsLogger
    {
        public void Log(
            OperationsLogLevel level,
            string source,
            string text,
            Exception? exception = null,
            string? correlationId = null)
        {
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
