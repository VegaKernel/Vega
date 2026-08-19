using NUnit.Framework;
using TShockAPI;
using TShockNG.Core.Commands;

namespace TShockLauncher.Tests.Core;

[TestFixture]
public sealed class ArchitectureBoundaryTests
{
    [Test]
    public void Core_DoesNotReferenceTShockApi()
    {
        var references = typeof(CommandDispatcher).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.That(references, Does.Not.Contain("TShockAPI"));
    }

    [Test]
    public void TShockApi_DoesNotReferenceCore()
    {
        var references = typeof(TShock).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name)
            .ToArray();

        Assert.That(references, Does.Not.Contain("TShockNG.Core"));
    }
}
