using NUnit.Framework;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class ServerCommandAuditTests
{
    private const string TerraZRevision = "81175b934497f9ef0ee5b5200334e4260b0cc06c";

    [Test]
    public void Phase25CommandLedgerEnumeratesAllPinnedTerraZRegistrations()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(root, "docs", "server-command-audit.md");
        Assert.That(File.Exists(path), Is.True, "Phase 25 must retain the command sub-ledger.");

        string text = File.ReadAllText(path);
        Assert.That(text, Does.Contain(TerraZRevision));

        string[] rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("| command-", StringComparison.Ordinal))
            .ToArray();

        Assert.That(rows, Has.Length.EqualTo(95),
            "The pinned TerraZ Commands.InitCommands registry has 95 registrations; refresh the ledger explicitly when the pin changes.");

        string[] ids = rows.Select(static row => row.Split('|')[1].Trim()).ToArray();
        string[] canonicalNames = rows.Select(static row => row.Split('|')[2].Trim()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(ids.Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(95));
            Assert.That(canonicalNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(), Is.EqualTo(95));
            Assert.That(rows.All(static row => row.Split('|').Length >= 15), Is.True,
                "Every command row must retain aliases, permission, console, handler, parsing, side effects, audit, dependencies, decision, status and evidence columns.");
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
