using NUnit.Framework;
using Vega.Core.Configuration;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class VegaServerConfigurationTests
{
    private string directory = null!;

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), "vega-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
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
    public void CommandLineOverridesEnvironmentWhichOverridesServerFile()
    {
        string path = Write(
            """
            {
              "worldPath": "file.wld",
              "port": 7000,
              "maximumPlayers": 8,
              "password": "file-secret",
              "defaultCulture": "en-US",
              "uiMode": "plain",
              "worldImageEnabled": false
            }
            """);
        var environment = new Dictionary<string, string>
        {
            ["VEGA_WORLD"] = "environment.wld",
            ["VEGA_PORT"] = "7001",
            ["VEGA_PASSWORD"] = "environment-secret",
            ["VEGA_WORLD_IMAGE"] = "on"
        };

        EffectiveVegaServerConfiguration effective = VegaServerConfigurationStore.Compose(
            path,
            ["-port", "7002", "--vega-ui=tui"],
            name => environment.GetValueOrDefault(name));

        Assert.Multiple(() =>
        {
            Assert.That(effective.Values["port"],
                Is.EqualTo(new EffectiveConfigurationValue("7002", "command-line")));
            Assert.That(effective.Values["world"].Source, Is.EqualTo("environment:VEGA_WORLD"));
            Assert.That(effective.Values["password"].DisplayValue, Is.EqualTo("<set>"));
            Assert.That(effective.Values["ui"].Source, Is.EqualTo("command-line"));
            Assert.That(effective.Values["worldImage"].DisplayValue, Is.EqualTo("on"));
            Assert.That(effective.Arguments, Does.Contain("environment.wld"));
            Assert.That(effective.Arguments.Count(argument => argument == "-port"), Is.EqualTo(1));
        });
    }

    [Test]
    public void MissingWorldLeavesInteractiveSelectionAvailable()
    {
        string path = Write("{}");

        EffectiveVegaServerConfiguration effective = VegaServerConfigurationStore.Compose(
            path,
            Array.Empty<string>(),
            _ => null);

        Assert.Multiple(() =>
        {
            Assert.That(effective.Arguments, Does.Not.Contain("-world"));
            Assert.That(effective.Values["world"].DisplayValue, Is.EqualTo("<default>"));
        });
    }

    [TestCase("{ \"port\": 0 }")]
    [TestCase("{ \"maximumPlayers\": 999 }")]
    [TestCase("{ \"defaultCulture\": \"definitely-not-a-culture\" }")]
    public void InvalidValueReportsTheRejectedProperty(string json)
    {
        string path = Write(json);

        Assert.That(
            () => VegaServerConfigurationStore.Compose(path, Array.Empty<string>(), _ => null),
            Throws.TypeOf<InvalidDataException>());
    }

    private string Write(string json)
    {
        string path = Path.Combine(directory, "server.json");
        File.WriteAllText(path, json);
        return path;
    }
}
