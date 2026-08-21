using NUnit.Framework;
using Vega.Modules.AutoWorldCleaner;
using Vega.Operations.Logging;

namespace Vega.Runtime.Tests.Modules;

[TestFixture]
public sealed class AutoWorldCleanerOptionsTests
{
    [Test]
    public void DefaultsKeepAutomaticCleanupOptIn()
    {
        AutoWorldCleanerOptions options = AutoWorldCleanerOptions.Default;

        Assert.Multiple(() =>
        {
            Assert.That(options.Enabled, Is.False);
            Assert.That(options.IntervalMinutes, Is.EqualTo(30));
            Assert.That(options.OnlyWhenPlayersOnline, Is.True);
            Assert.That(options.Items, Is.EqualTo(new AutoWorldCleanerItemOptions(true, 200, 300)));
            Assert.That(options.Projectiles, Is.EqualTo(new AutoWorldCleanerProjectileOptions(true, 700, true, true)));
        });
    }

    [Test]
    public void ConfigurationLoadsAllCleanerParameters()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "vega-auto-world-cleaner-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
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
                """);
            var logger = new StructuredOperationsLogger([new BoundedOperationsLogStore()]);

            AutoWorldCleanerOptions options = AutoWorldCleanerOptionsStore.Load(path, logger);

            Assert.Multiple(() =>
            {
                Assert.That(options.Enabled, Is.True);
                Assert.That(options.IntervalMinutes, Is.EqualTo(12));
                Assert.That(options.OnlyWhenPlayersOnline, Is.False);
                Assert.That(options.Items, Is.EqualTo(new AutoWorldCleanerItemOptions(true, 42, 90)));
                Assert.That(options.Projectiles, Is.EqualTo(new AutoWorldCleanerProjectileOptions(false, 321, false, false)));
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
        var candidate = new AutoWorldCleanerOptions(
            true,
            int.MaxValue,
            true,
            new AutoWorldCleanerItemOptions(true, 0, int.MaxValue),
            new AutoWorldCleanerProjectileOptions(true, int.MaxValue, true, true));

        AutoWorldCleanerOptions options = AutoWorldCleanerOptions.Normalize(candidate);

        Assert.Multiple(() =>
        {
            Assert.That(options.IntervalMinutes, Is.EqualTo(1440));
            Assert.That(options.Items.MinimumActiveCount, Is.EqualTo(1));
            Assert.That(options.Items.MinimumAgeSeconds, Is.EqualTo(86400));
            Assert.That(options.Projectiles.MinimumActiveCount, Is.EqualTo(5000));
        });
    }
}
