using NUnit.Framework;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class RuntimeDependencyTests
{
    [Test]
    public void OperationsAndTerminalUiStayIndependentFromTerrariaAndLegacyApis()
    {
        var repositoryRoot = FindRepositoryRoot();
        string[] directories =
        [
            Path.Combine(repositoryRoot, "src", "Vega.Operations"),
            Path.Combine(repositoryRoot, "src", "Vega.TerminalUI")
        ];
        string[] forbiddenTokens =
        [
            "Terraria.Main",
            "Main.player",
            "OTAPI",
            "TerrariaApi.Server",
            "TShockAPI",
            "TSPlayer"
        ];

        string[] offenders = directories
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => forbiddenTokens.Any(token =>
                File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.That(offenders, Is.Empty,
            $"Operations/UI may consume only snapshots and application contracts. Offenders: {string.Join(", ", offenders)}");
    }

    [Test]
    public void TerminalGuiDependencyIsContainedToTerminalUiProject()
    {
        var repositoryRoot = FindRepositoryRoot();
        string coreProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Core", "Vega.Core.csproj"));
        string contractsProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Contracts", "Vega.Contracts.csproj"));
        string operationsProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Operations", "Vega.Operations.csproj"));
        string terminalProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.TerminalUI", "Vega.TerminalUI.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(coreProject, Does.Not.Contain("Terminal.Gui"));
            Assert.That(contractsProject, Does.Not.Contain("Terminal.Gui"));
            Assert.That(operationsProject, Does.Not.Contain("Terminal.Gui"));
            Assert.That(terminalProject, Does.Contain("Terminal.Gui"));
        });
    }

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
    public void VegaRepository_has_no_TSAPI_or_TShock_runtime_projects()
    {
        var repositoryRoot = FindRepositoryRoot();
        string solution = File.ReadAllText(Path.Combine(repositoryRoot, "Vega.sln"));
        string host = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Terraria", "TerrariaServerHost.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(Directory.Exists(Path.Combine(repositoryRoot, "src", "TShockAPI")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(repositoryRoot, "external", "TSAPI")), Is.False);
            Assert.That(solution, Does.Not.Contain("TShockAPI"));
            Assert.That(solution, Does.Not.Contain("TSAPI"));
            Assert.That(host, Does.Not.Contain("LegacyTsapiBridge"));
            Assert.That(host, Does.Not.Contain("TerrariaApi.Server"));
        });
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

    [Test]
    public void Vega_native_packet_security_attaches_without_legacy_bridge()
    {
        var repositoryRoot = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Terraria", "TerrariaServerHost.cs"));
        var nativeAttach = host.IndexOf("playerPacketSecurityHooks.Attach();", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(nativeAttach, Is.GreaterThanOrEqualTo(0));
            Assert.That(host, Does.Not.Contain("LegacyTsapiBridge"));
        });
    }

    [Test]
    public void Vega_native_packet_security_owns_migrated_player_identity_packets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Hooking",
            "NativePlayerPacketSecurityHooks.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("PacketTypes.PlayerHp"));
            Assert.That(source, Does.Contain("PacketTypes.PlayerMana"));
            Assert.That(source, Does.Contain("PacketTypes.PlayerTeam"));
            Assert.That(source, Does.Contain("PacketTypes.TogglePvp"));
            Assert.That(source, Does.Not.Contain("GetDataHandlers"));
        });
    }

    [Test]
    public void Vega_native_console_commands_attach_without_legacy_bridge()
    {
        var repositoryRoot = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Terraria", "TerrariaServerHost.cs"));
        var nativeAttach = host.IndexOf("consoleCommandHooks.Attach();", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(nativeAttach, Is.GreaterThanOrEqualTo(0));
            Assert.That(host, Does.Not.Contain("LegacyTsapiBridge"));
        });
    }

    [Test]
    public void Vega_native_player_commands_attach_without_legacy_bridge()
    {
        var repositoryRoot = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Terraria", "TerrariaServerHost.cs"));
        var nativeAttach = host.IndexOf("playerCommandHooks.Attach();", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(nativeAttach, Is.GreaterThanOrEqualTo(0));
            Assert.That(host, Does.Not.Contain("LegacyTsapiBridge"));
        });
    }

    [Test]
    public void Vega_native_player_commands_use_Multiplicity_and_modern_player_contracts()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Hooking",
            "NativePlayerCommandHooks.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("PacketViewParser.TryParsePayloadBounded"));
            Assert.That(source, Does.Contain("NetTextModuleView"));
            Assert.That(source, Does.Contain("IPlayerMessenger"));
            Assert.That(source, Does.Not.Contain("Commands.HandleCommand"));
            Assert.That(source, Does.Not.Contain("TSPlayer"));
        });
    }

    [Test]
    public void ProductionModules_use_Vega_capabilities_and_shared_protocol()
    {
        string repositoryRoot = FindRepositoryRoot();
        string customItems = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
			"Items",
            "CustomItemPacketFactory.cs"));
        string pvp = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Modules.Pvp",
            "PvpPacketPolicy.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(customItems, Does.Contain("Multiplicity.Packets"));
            Assert.That(customItems, Does.Contain("TweakItem"));
            Assert.That(customItems, Does.Not.Contain("PacketWriter"));
            Assert.That(pvp, Does.Contain("IPacketProcessor<PacketView>"));
            Assert.That(pvp, Does.Contain("IPlayerCombatDirectory"));
            Assert.That(pvp, Does.Not.Contain("TSPlayer"));
        });
    }

    [Test]
    public void TypedExtensionStateIsOptInAndHasOneConcreteMigratedConsumer()
    {
        string repositoryRoot = FindRepositoryRoot();
        string playerSnapshot = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Contracts",
            "Players",
            "PlayerSnapshot.cs"));
        string pvpProject = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Modules.Pvp",
            "Vega.Modules.Pvp.csproj"));
        string[] consumers = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("IExtensionStateStore", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(playerSnapshot, Does.Not.Contain("ExtensionState"));
            Assert.That(pvpProject, Does.Not.Contain("Vega.Core"));
            Assert.That(consumers.Count(path => path.Contains("Vega.Modules.Pvp", StringComparison.Ordinal)), Is.EqualTo(2));
            Assert.That(consumers.Any(path => path.Contains("Vega.Core\\Extensions", StringComparison.Ordinal)), Is.True);
        });
    }

    [Test]
    public void ClusterTransportAndScenarioImplementationStayOutsideCoreAndContracts()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] protectedProjects =
        [
            Path.Combine(repositoryRoot, "src", "Vega.Contracts"),
            Path.Combine(repositoryRoot, "src", "Vega.Core")
        ];
        string[] concreteTransportPackages =
        [
            "StackExchange.Redis",
            "NATS.Client",
            "RabbitMQ.Client",
            "MassTransit"
        ];
        string[] offenders = protectedProjects
            .SelectMany(path => Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories))
            .Where(path => concreteTransportPackages.Any(package =>
                File.ReadAllText(path).Contains(package, StringComparison.OrdinalIgnoreCase)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(offenders, Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(repositoryRoot, "src", "Vega.Cluster", "Transport")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(repositoryRoot, "src", "Vega.Cluster", "Scenarios")), Is.True);
        });
    }

    [Test]
    public void ReplicatedBanHandlersCannotRepublishAndStandaloneUsesUnavailableClient()
    {
        string repositoryRoot = FindRepositoryRoot();
        string banHandlers = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Cluster",
            "Scenarios",
            "Bans",
            "GlobalBanReplication.cs"));
        string handlerSection = banHandlers[banHandlers.IndexOf(
            "public sealed class GlobalBanCreatedHandler",
            StringComparison.Ordinal)..];
        string runtime = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Runtime", "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(handlerSection, Does.Not.Contain("IClusterPublisher"));
            Assert.That(handlerSection, Does.Not.Contain("PublishAsync"));
            Assert.That(runtime, Does.Contain("UnavailableClusterClient.Instance"));
        });
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
