using NUnit.Framework;
using TShockNG.Core.Commands;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class CoreDependencyTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Terraria",
        "OTAPI",
        "TShockAPI"
    ];

    [Test]
    public void VegaCore_does_not_reference_game_runtime_or_legacy_api_assemblies()
    {
        var referencedAssemblies = typeof(CommandDispatcher).Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        var forbidden = referencedAssemblies
            .Where(name => ForbiddenAssemblyPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.That(forbidden, Is.Empty,
            $"Vega.Core must stay independent from Terraria, OTAPI, TSAPI and TShockAPI. Forbidden references: {string.Join(", ", forbidden)}");
    }
}
