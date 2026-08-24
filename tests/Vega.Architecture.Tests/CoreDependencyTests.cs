using System.Reflection;
using NUnit.Framework;
using Vega.Core.Commands;
using Vega.Core.Events;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class CoreDependencyTests
{
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Terraria",
        "TerrariaRuntime",
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
            $"Vega.Core must stay independent from Terraria, TerrariaRuntime, TSAPI and TShockAPI. Forbidden references: {string.Join(", ", forbidden)}");
    }

    [Test]
    public void VegaCore_uses_runtime_async()
    {
        var method = typeof(AsyncEventPipeline<>).GetMethod(nameof(AsyncEventPipeline<object>.DispatchAsync));

        Assert.That(method, Is.Not.Null);
        Assert.That(method!.GetMethodImplementationFlags().HasFlag(MethodImplAttributes.Async), Is.True);
    }
}
