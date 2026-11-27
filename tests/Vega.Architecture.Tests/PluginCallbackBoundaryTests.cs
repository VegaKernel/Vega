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
            "IPluginPlayers.OnJoined", "IPluginPlayers.OnLeft", "IPluginPlayers.OnEnteredWorld", "IPluginPlayers.OnClosing",
            "IPluginActivity.OnAuthenticationEstablished", "IPluginActivity.OnAuthenticationEnded",
            "IPluginActivity.OnPublicChatAccepted", "IPluginActivity.OnCommandCompleted",
            "IPluginActivity.BeforePlayerCommand", "IPluginActivity.BeforePublicChat",
            "IPluginIdentity.OnAccountChanged", "IPluginIdentity.OnGroupChanged", "IPluginIdentity.OnTemporaryGroupChanged",
            "IPluginResources.OnCleanup", "IPluginTasks.Post", "IPluginTasks.Every",
            "IPluginTasks.RunIo", "IPluginTasks.TryRunCpu", "IPluginConfiguration.Use", "IPluginStorage.LoadJson",
            "IPluginServices.Register",
            "IPluginCluster.Subscribe",
            "IPluginPackets.AllowInbound",
            "IPluginRest.RegisterQuery", "IPluginRest.RegisterAction",
            "IPluginWorldEvents.OnTickCompleted", "IPluginWorldEvents.OnSaveCompleted",
            "IPluginWorldEvents.OnHardmodeChanged",
            "IPluginWorldEvents.OnPlayerVitalsChanged", "IPluginWorldEvents.OnPlayerDeathObserved",
            "IPluginWorldEvents.OnPlayerRespawnObserved", "IPluginWorldEvents.OnPlayerDeathCountersChanged",
            "IPluginWorldEvents.OnPlayerPvpChanged", "IPluginWorldEvents.OnPlayerBuffsChanged",
            "IPluginWorldEvents.OnPlayerHurtCompleted",
            "IPluginWorldEvents.OnTileInteractionRequested", "IPluginWorldEvents.AllowTileInteraction",
            "IPluginWorldEvents.OnPlayerTileChangesCommitted",
            "IPluginWorldEvents.OnPlayerPvpChangeRequested", "IPluginWorldEvents.AllowPlayerPvpChange",
            "IPluginWorldEvents.OnRegionEntered", "IPluginWorldEvents.OnRegionLeft", "IPluginWorldEvents.OnRegionChanged",
            "IPluginWorldEvents.OnNpcCreating", "IPluginWorldEvents.OnNpcKilled", "IPluginWorldEvents.OnNpcTransforming",
            "IPluginWorldEvents.OnNpcStrikeCompleted", "IPluginWorldEvents.OnNpcStrikeCompletedWithSource",
            "IPluginWorldEvents.OnNpcLootDropped", "IPluginWorldEvents.OnProjectileSynchronized", "IPluginWorldEvents.OnProjectileRemoved",
            "IPluginWorldEvents.AllowNpcTransform", "IPluginWorldEvents.AllowNpcLoot", "IPluginWorldEvents.OnNpcActorInteracted",
            "IPluginWorldEvents.OnNpcActorPurchased", "IPluginWorldEvents.OnNpcActorDialogueChoice",
            "IPluginWorldEvents.OnNpcActorQuest", "IPluginWorldEvents.OnNpcActorThink"
        }), "A new ordinary SDK delegate surface requires a fault-boundary audit and regression.");
        // Storage defaults/validation run synchronously at the direct plugin call site and are
        // not retained as host callbacks. Managed configuration diagnostics are retained and guarded below.

        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "Vega.sln"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        string scope = string.Join("\n", Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Vega.Core", "Modules"), "HotModuleRegistrationScope*.cs")
            .Select(File.ReadAllText));
        string activity = File.ReadAllText(Path.Combine(root.FullName, "src", "Vega.Core", "Modules", "HotModuleRegistrationScope.Activity.cs"));
        string identity = File.ReadAllText(Path.Combine(root.FullName, "src", "Vega.Core", "Modules", "HotModuleRegistrationScope.Identity.cs"));
        string cluster = File.ReadAllText(Path.Combine(root.FullName, "src", "Vega.Core", "Modules", "HotModuleRegistrationScope.Cluster.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(scope, Does.Contain("var guarded = new GuardedCommandHandler(owner, handler);"));
            Assert.That(scope, Does.Contain("owner.RegisterOwned(() => commands.Register(guarded))"));
            Assert.That(scope, Does.Contain("owner.executionGuard.InvokeCommandAsync("));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(\"players.joined\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(\"players.left\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(\"players.entered-world\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(\"players.closing\""));
            // Cleanup callbacks run after guard drain; their failures must surface as failed retirement, not be swallowed by the closed guard.
            Assert.That(scope, Does.Contain("owner.RegisterCleanup(cleanup)"));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke($\"task:{name}\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.Record($\"io:{name}\""));
            Assert.That(scope, Does.Contain("InvokeDiagnostic($\"config:{logicalName}:validate\""));
            Assert.That(scope, Does.Contain("owner.executionGuard.TryInvoke(operation, handler, value)"));
            Assert.That(scope, Does.Contain("owner.executionGuard.Allows(operation, allow, value)"));
            Assert.That(scope, Does.Contain("owner.RegisterOwned(() => events.SubscribePolicy<T>"));
            Assert.That(activity, Does.Contain("owner.executionGuard.TryInvoke(operation, liveHandler, value)"));
            Assert.That(activity, Does.Contain("owner.executionGuard.InvokePolicy(operation, live, value)"));
            Assert.That(activity, Does.Contain("Interlocked.Exchange(ref activeHandler, null)"));
            Assert.That(identity, Does.Contain("owner.executionGuard.TryInvoke(operation, current, snapshot)"));
            Assert.That(identity, Does.Contain("Interlocked.Exchange(ref active, null)"));
            Assert.That(cluster, Does.Contain("owner.RegisterOwned(() => registry.Register("));
            Assert.That(cluster, Does.Contain("owner.executionGuard.InvokeServiceAsync("));
            Assert.That(cluster, Does.Contain("owner.RunScopedAsync("));
        });
    }
}
