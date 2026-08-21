using NUnit.Framework;
using Vega.Operations.Logging;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class WorldMaintenanceOptionsTests
{
    [Test]
    public void DefaultsPreserveTerrariaAutosave()
    {
        WorldMaintenanceOptions options = WorldMaintenanceOptions.Default;

        Assert.Multiple(() =>
        {
            Assert.That(options.AutoSave.Enabled, Is.True);
            Assert.That(options.AutoSave.IntervalMinutes, Is.EqualTo(10));
        });
    }

    [Test]
    public void ConfigurationLoadsAutosaveParameters()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vega-world-maintenance-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "autoSave": { "enabled": false, "intervalMinutes": 3 }
                }
                """);
            var logger = new StructuredOperationsLogger([new BoundedOperationsLogStore()]);

            WorldMaintenanceOptions options = WorldMaintenanceOptionsStore.Load(path, logger);

            Assert.That(options.AutoSave, Is.EqualTo(new WorldAutoSaveOptions(false, 3)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void UnsafeAutosaveIntervalIsBounded()
    {
        var candidate = new WorldMaintenanceOptions(new WorldAutoSaveOptions(true, 0));

        WorldMaintenanceOptions options = WorldMaintenanceOptions.Normalize(candidate);

        Assert.That(options.AutoSave.IntervalMinutes, Is.EqualTo(1));
    }
}
