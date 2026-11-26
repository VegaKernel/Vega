using NUnit.Framework;
using Vega.PluginSdk;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class PluginCallbackBoundaryTests
{
    [Test]
    public void OrdinarySdkCallbackSurfacesRequireAnExplicitGuardReview()
    {
        string[] callbackMethods = typeof(IPluginTasks).Assembly.GetExportedTypes()
            .Where(type => type.IsInterface && type.Name.StartsWith("IPlugin", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods().Where(method => method.GetParameters()
                .Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)))
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToArray();
        Assert.That(callbackMethods, Is.EquivalentTo(new[]
        {
            "IPluginPlayers.OnJoined", "IPluginPlayers.OnLeft", "IPluginTasks.Post", "IPluginTasks.Every",
            "IPluginTasks.RunIo", "IPluginConfiguration.Use", "IPluginStorage.LoadJson"
        }), "A new ordinary SDK delegate surface requires a fault-boundary audit and regression.");
        // Storage defaults/validation run synchronously at the direct plugin call site and are
        // not retained as host callbacks. Managed configuration diagnostics are retained and guarded below.

        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "Vega.sln"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        string scope = File.ReadAllText(Path.Combine(root!.FullName, "src", "Vega.Core", "Modules", "HotModuleRegistrationScope.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(scope, Does.Contain("commands.Register(new GuardedCommandHandler(owner, handler))"));
            Assert.That(scope, Does.Contain("owner.executionGuard.InvokeCommandAsync("));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(\"players.joined\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(\"players.left\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke($\"task:{name}\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.Record($\"io:{name}\""));
            Assert.That(scope, Does.Contain("InvokeDiagnostic($\"config:{logicalName}:validate\""));
        });
    }
}
