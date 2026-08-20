using NUnit.Framework;
using Vega.Operations.Logging;
using Vega.Terraria.Worlds;

namespace Vega.Runtime.Tests.Worlds;

[TestFixture]
public sealed class WorldMaintenanceOptionsTests
{
    [Test]
    public void DefaultsPreserveTerrariaAutosaveAndKeepCleanupOptIn()
    {
        WorldMaintenanceOptions options = WorldMaintenanceOptions.Default;

        Assert.Multiple(() =>
        {
            Assert.That(options.AutoSave.Enabled, Is.True);
            Assert.That(options.AutoSave.IntervalMinutes, Is.EqualTo(10));
            Assert.That(options.Cleanup.Enabled, Is.False);
            Assert.That(options.Cleanup.Projectiles.ExcludeMinions, Is.True);
            Assert.That(options.Cleanup.Projectiles.ExcludeSentries, Is.True);
        });
    }

    [Test]
    public void ConfigurationLoadsAllAutosaveAndCleanupParameters()
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
                  "autoSave": { "enabled": false, "intervalMinutes": 3 },
                  "cleanup": {
                    "enabled": true,
                    "intervalMinutes": 12,
                    "onlyWhenPlayersOnline": false,
                    "items": {
                      "enabled": true,
                      "minimumActiveCount": 42,
                      "minimumAgeSeconds": 90
                    },
                    "projectiles": {
                      "enabled": false,
                      "minimumActiveCount": 321,
                      "excludeMinions": false,
                      "excludeSentries": false
                    }
                  }
                }
                """);
            var logger = new StructuredOperationsLogger([new BoundedOperationsLogStore()]);

            WorldMaintenanceOptions options = WorldMaintenanceOptionsStore.Load(path, logger);

            Assert.Multiple(() =>
            {
                Assert.That(options.AutoSave, Is.EqualTo(new WorldAutoSaveOptions(false, 3)));
                Assert.That(options.Cleanup.Enabled, Is.True);
                Assert.That(options.Cleanup.IntervalMinutes, Is.EqualTo(12));
                Assert.That(options.Cleanup.OnlyWhenPlayersOnline, Is.False);
                Assert.That(
                    options.Cleanup.Items,
                    Is.EqualTo(new WorldItemCleanupOptions(true, 42, 90)));
                Assert.That(
                    options.Cleanup.Projectiles,
                    Is.EqualTo(new WorldProjectileCleanupOptions(false, 321, false, false)));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void UnsafeIntervalsAndThresholdsAreBounded()
    {
        var candidate = new WorldMaintenanceOptions(
            new WorldAutoSaveOptions(true, 0),
            new WorldCleanupOptions(
                true,
                int.MaxValue,
                true,
                new WorldItemCleanupOptions(true, 0, int.MaxValue),
                new WorldProjectileCleanupOptions(true, int.MaxValue, true, true)));

        WorldMaintenanceOptions options = WorldMaintenanceOptions.Normalize(candidate);

        Assert.Multiple(() =>
        {
            Assert.That(options.AutoSave.IntervalMinutes, Is.EqualTo(1));
            Assert.That(options.Cleanup.IntervalMinutes, Is.EqualTo(1440));
            Assert.That(options.Cleanup.Items.MinimumActiveCount, Is.EqualTo(1));
            Assert.That(options.Cleanup.Items.MinimumAgeSeconds, Is.EqualTo(86400));
            Assert.That(options.Cleanup.Projectiles.MinimumActiveCount, Is.EqualTo(5000));
        });
    }
}
