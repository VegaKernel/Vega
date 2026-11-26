using NUnit.Framework;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class ServerBehaviorAuditTests
{
    private const string TerraZRevision = "81175b934497f9ef0ee5b5200334e4260b0cc06c";
    private const string TerrariaSha256 = "D87E3FAF08637F6BE8882C63E7F11FB7E792B0230006309618473ECE0F863E1E";

    [Test]
    public void Phase25LedgerPinsEvidenceAndEnumeratesAllTerraZPacketRegistrations()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "docs", "server-behavior-audit.md");
        Assert.That(File.Exists(path), Is.True, "Phase 25 must retain a durable behavior audit ledger.");

        string text = File.ReadAllText(path);
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain(TerraZRevision));
            Assert.That(text, Does.Contain(TerrariaSha256));
            Assert.That(text, Does.Contain("Historical protocol baseline: `326`"));
            Assert.That(text, Does.Contain("current native baseline Terraria 1.4.6.1 / protocol 329"));
            Assert.That(text, Does.Contain("95 built-in `Command` registrations"));
        });

        string[] rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("| packet-", StringComparison.Ordinal))
            .ToArray();

        Assert.That(rows, Has.Length.EqualTo(71),
            "The pinned TerraZ GetDataHandlers registry has 71 packet registrations; refresh the ledger explicitly when the pin changes.");

        string[] ids = rows.Select(static row => row.Split('|')[1].Trim()).ToArray();
        string[] registrations = rows.Select(static row => row.Split('|')[3].Trim()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ids.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(71));
            Assert.That(registrations.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(71));
            Assert.That(rows.All(static row => row.Contains("`81175b9`", StringComparison.Ordinal)), Is.True,
                "Every packet row must record the pinned source revision.");
            Assert.That(rows.All(static row => row.Split('|').Length >= 12), Is.True,
                "Every packet row must retain source, handler, classification, invariant, decision, owner, status and evidence columns.");
        });
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "docs", "roadmap.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Vega repository root from the architecture test directory.");
    }
}
