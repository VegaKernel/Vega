using System.Text.Json;
using NUnit.Framework;
using Vega.Runtime;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class DefaultConfigurationBootstrapTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(
            Path.GetTempPath(),
            "vega-config-bootstrap-tests",
            Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void EnsureMissingCreatesEveryCanonicalConfigAsValidJson()
    {
        IReadOnlyList<string> created = DefaultConfigurationBootstrap.EnsureMissing(directory);

        Assert.That(created, Has.Count.EqualTo(12));
        Assert.That(created, Does.Contain("server.json"));
        Assert.That(created, Does.Contain("security.json"));
        Assert.That(created, Does.Contain("tui.json"));

        foreach (string fileName in created)
        {
            string path = Path.Combine(directory, fileName);
            Assert.That(File.Exists(path), Is.True, fileName);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(document.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object), fileName);
        }
    }

    [Test]
    public void EnsureMissingNeverOverwritesExistingOperatorConfig()
    {
        Directory.CreateDirectory(directory);
        string serverPath = Path.Combine(directory, "server.json");
        const string sentinel = "{\n  \"operatorValue\": \"keep-me\"\n}\n";
        File.WriteAllText(serverPath, sentinel);

        IReadOnlyList<string> created = DefaultConfigurationBootstrap.EnsureMissing(directory);

        Assert.That(File.ReadAllText(serverPath), Is.EqualTo(sentinel));
        Assert.That(created, Does.Not.Contain("server.json"));
        Assert.That(created, Has.Count.EqualTo(11));
    }

    [Test]
    public void EnsureMissingIsIdempotent()
    {
        DefaultConfigurationBootstrap.EnsureMissing(directory);

        IReadOnlyList<string> second = DefaultConfigurationBootstrap.EnsureMissing(directory);

        Assert.That(second, Is.Empty);
        Assert.That(Directory.EnumerateFiles(directory, "*.tmp"), Is.Empty);
    }
}
