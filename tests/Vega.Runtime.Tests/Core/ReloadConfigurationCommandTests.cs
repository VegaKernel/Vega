using NUnit.Framework;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Items;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Configuration;
using Vega.Modules.Administration;
using Vega.Modules.Restrictions;
using Vega.Regions;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class ReloadConfigurationCommandTests
{
    [Test]
    public async Task ReloadPublishesValidPoliciesIndependentlyAndRetainsFailedOrMissingPolicies()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-reload-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string configPath = Path.Combine(root, "server.json");
            string whitelistPath = Path.Combine(root, "whitelist.json");
            string restrictionsPath = Path.Combine(root, "restrictions.json");
            string buildPath = Path.Combine(root, "build-protection.json");
            var configuration = new VegaServerConfigurationState(configPath, [], _ => null);
            var whitelist = new ConnectionWhitelist(whitelistPath);
            var restrictions = new RestrictionStore(restrictionsPath, 100, 100, 100);
            var world = new WorldId("isolated");
            var protection = new WorldBuildProtection(buildPath, world, new Authorization(), () => null);
            await restrictions.SetAsync(RestrictedContentKind.Item, 15, true);
            await protection.ChangeAsync(state => state with { GlobalDisabled = true });
            File.WriteAllText(whitelistPath, """{"enabled":true,"addresses":["192.0.2.10"]}""");
            File.WriteAllText(restrictionsPath, """[{"kind":0,"type":999}]""");
            File.Delete(buildPath);

            var reload = new ReloadConfigurationCommand(configuration, world,
                [("whitelist", whitelist.ReloadAsync), ("restrictions", restrictions.ReloadAsync), ("build", protection.ReloadAsync)]);
            var dispatcher = new CommandDispatcher([reload, new ConfigurationCommand(configuration, reload)]);
            var invocation = new CommandInvocation("reload", [], new Principal(false), new Output()) { WorldId = world };
            Assert.That((await dispatcher.DispatchAsync(invocation)).Status, Is.EqualTo(CommandExecutionStatus.Forbidden));
            invocation = invocation with { Principal = new Principal(true) };
            Assert.That((await dispatcher.DispatchAsync(invocation with { WorldId = WorldId.Primary })).Status, Is.EqualTo(CommandExecutionStatus.Failed));
            Assert.That(whitelist.Enabled, Is.False);
            File.WriteAllText(configPath, """{"port":0}""");
            Assert.That((await dispatcher.DispatchAsync(invocation)).ErrorCode, Is.EqualTo("config.invalid"));
            Assert.That(whitelist.Enabled, Is.False);
            File.WriteAllText(configPath, """{"port":7778}""");
            Assert.That((await dispatcher.DispatchAsync(invocation)).ErrorCode, Is.EqualTo("config.reload.partial"));
            Assert.That(whitelist.Enabled, Is.True);
            Assert.That(whitelist.Addresses, Is.EqualTo(new[] { "192.0.2.10" }));
            Assert.That(restrictions.Contains(RestrictedContentKind.Item, 15), Is.True);
            Assert.That(protection.Current.GlobalDisabled, Is.True);

            File.WriteAllText(restrictionsPath, "[]");
            File.WriteAllText(buildPath, """{"globalDisabled":false,"spawnProtected":true,"spawnRadius":25}""");
            Assert.That((await dispatcher.DispatchAsync(invocation with { Name = "config", Arguments = ["reload"] })).Status,
                Is.EqualTo(CommandExecutionStatus.Success));
            Assert.That(restrictions.HasRestrictions, Is.False);
            Assert.That(protection.Current, Is.EqualTo(new BuildProtectionState(false, true, 25)));
            Assert.That(configuration.Current.Values["port"].DisplayValue, Is.Not.EqualTo("7778"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class Authorization : IPlayerAuthorizationService
    {
        public bool HasPermission(PlayerId playerId, string permission) => false;
    }

    private sealed class Principal(bool allowed) : ICommandPrincipal
    {
        public string Name => "test";
        public bool IsServer => false;
        public bool HasPermission(string permission) => allowed;
    }

    private sealed class Output : ICommandOutput
    {
        public ValueTask InfoAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask SuccessAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask WarningAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask ErrorAsync(string message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
