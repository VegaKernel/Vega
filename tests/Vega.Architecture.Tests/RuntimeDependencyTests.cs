using NUnit.Framework;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class RuntimeDependencyTests
{
    [Test]
    public void VegaRuntime_does_not_reference_TSAPI_directly()
    {
        var repositoryRoot = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Runtime", "Vega.Runtime.csproj"));
        var program = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Runtime", "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Not.Contain("TerrariaServerAPI"));
            Assert.That(project, Does.Not.Contain("external\\TSAPI"));
            Assert.That(program, Does.Not.Contain("TerrariaApi.Server"));
        });
    }

    [Test]
    public void VegaTerraria_contains_TSAPI_usage_in_legacy_bridge_only()
    {
        var repositoryRoot = FindRepositoryRoot();
        var terrariaDirectory = Path.Combine(repositoryRoot, "src", "Vega.Terraria");

        var offenders = Directory
            .EnumerateFiles(terrariaDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(
                Path.GetFileName(path),
                "LegacyTsapiBridge.cs",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("TerrariaApi.Server", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.That(
            offenders,
            Is.Empty,
            $"TSAPI is transitional and must remain isolated to LegacyTsapiBridge. Offenders: {string.Join(", ", offenders)}");
    }

    [Test]
    public void VegaTerraria_player_boundary_does_not_reference_TShockAPI()
    {
        var repositoryRoot = FindRepositoryRoot();
        var playerDirectory = Path.Combine(repositoryRoot, "src", "Vega.Terraria", "Players");

        var offenders = Directory
            .EnumerateFiles(playerDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("TShockAPI", StringComparison.Ordinal) ||
                       source.Contains("TSPlayer", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.That(
            offenders,
            Is.Empty,
            $"The Vega player boundary must stay Terraria-native. Offenders: {string.Join(", ", offenders)}");
    }

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Vega.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Vega repository root.");
    }
}
