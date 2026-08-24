using NUnit.Framework;
using System.Text.RegularExpressions;

namespace Vega.Architecture.Tests;

[TestFixture]
public sealed class RuntimeDependencyTests
{
    [Test]
    public void RegionsIsACompleteContractsOnlyVerticalSlice()
    {
        string repositoryRoot = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Regions", "Vega.Regions.csproj"));
        string[] regionSources = Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "src", "Vega.Regions"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        string[] coreSources = Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "src", "Vega.Core"), "*.cs", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("Vega.Contracts"));
            Assert.That(project, Does.Not.Contain("Vega.Core"));
            Assert.That(project, Does.Not.Contain("Vega.Persistence"));
            Assert.That(project, Does.Not.Contain("Vega.Terraria"));
            Assert.That(regionSources.Any(source => source.Contains("class RegionCommand", StringComparison.Ordinal)), Is.True);
            Assert.That(regionSources.Any(source => source.Contains("class RegionBuildAuthorizationPolicy", StringComparison.Ordinal)), Is.True);
            Assert.That(coreSources.Any(source => source.Contains("class RegionCommand", StringComparison.Ordinal)), Is.False);
            Assert.That(coreSources.Any(source => source.Contains("class RegionBuildAuthorizationPolicy", StringComparison.Ordinal)), Is.False);
        });
    }

    [Test]
    public void BackupOrchestrationRemainsAnIndependentContractsOnlyDomain()
    {
        string repositoryRoot = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Backups", "Vega.Backups.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("Vega.Contracts"));
            Assert.That(project, Does.Not.Contain("Vega.Core"));
            Assert.That(project, Does.Not.Contain("Vega.Persistence"));
            Assert.That(project, Does.Not.Contain("Vega.Terraria"));
        });
    }

    [Test]
    public void PersistenceProvidersStayOutsideCoreAndInitializeBeforeWorldLoading()
    {
        var repositoryRoot = FindRepositoryRoot();
        string persistenceDirectory = Path.Combine(repositoryRoot, "src", "Vega.Persistence");
        string[] persistenceSources = Directory
            .EnumerateFiles(persistenceDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();
        string coreProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Core", "Vega.Core.csproj"));
        string contractsProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Contracts", "Vega.Contracts.csproj"));
        string host = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Terraria", "TerrariaServerHost.cs"));

        int modulesRegistered = host.IndexOf(
            "lifecycle.Advance(RuntimeLifecycleStage.ModulesRegistered);",
            StringComparison.Ordinal);
        int persistenceInitialization = host.IndexOf(
            "InitializeRuntimeParticipants(startupParticipants, logger);",
            StringComparison.Ordinal);
        int terrariaMain = host.IndexOf(
            "global::Terraria.WindowsLaunch.Main(args);",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(
                persistenceSources.Any(source =>
                    source.Contains("TShockAPI", StringComparison.Ordinal) ||
                    source.Contains("TSPlayer", StringComparison.Ordinal) ||
                    source.Contains("Terraria.Main", StringComparison.Ordinal) ||
                    source.Contains("OTAPI", StringComparison.Ordinal)),
                Is.False,
                "Persistence must remain independent from Terraria and legacy APIs.");
            Assert.That(coreProject, Does.Not.Contain("Microsoft.Data.Sqlite"));
            Assert.That(coreProject, Does.Not.Contain("Npgsql"));
            Assert.That(coreProject, Does.Not.Contain("MySqlConnector"));
            Assert.That(contractsProject, Does.Not.Contain("Microsoft.Data.Sqlite"));
            Assert.That(contractsProject, Does.Not.Contain("Npgsql"));
            Assert.That(contractsProject, Does.Not.Contain("MySqlConnector"));
            Assert.That(modulesRegistered, Is.GreaterThanOrEqualTo(0));
            Assert.That(persistenceInitialization, Is.GreaterThan(modulesRegistered));
            Assert.That(terrariaMain, Is.GreaterThan(persistenceInitialization));
        });
    }

    [Test]
    public void WorldClockTouchesTerrariaOnlyFromTheGameTickAdapter()
    {
        var repositoryRoot = FindRepositoryRoot();
        string core = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Core", "Worlds", "MonotonicWorldClock.cs"));
        string adapter = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Terraria", "Worlds", "TerrariaWorldClockSynchronizer.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(core, Does.Not.Contain("Terraria.Main"));
            Assert.That(core, Does.Not.Contain("Task.Run"));
            Assert.That(adapter, Does.Contain("OnTickForThirdPartySoftwareOnly"));
            Assert.That(adapter, Does.Not.Contain("Task.Run"));
            Assert.That(adapter, Does.Not.Contain("System.Threading.Timer"));
        });
    }

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
    public void PublishedRuntimeSeparatesLibrariesFromServerPlugins()
    {
        var repositoryRoot = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Runtime", "Vega.Runtime.csproj"));
        string program = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Vega.Runtime", "Program.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("$(PublishDir)libs"));
            Assert.That(project, Does.Contain("$(PublishDir)ServerPlugins"));
            Assert.That(project, Does.Contain("$(PublishDir)Vega.Modules.*"));
            Assert.That(program, Does.Contain("Path.Combine(AppContext.BaseDirectory, \"libs\")"));
            Assert.That(program, Does.Contain("Path.Combine(AppContext.BaseDirectory, \"ServerPlugins\")"));
            Assert.That(program, Does.Not.Contain("Path.Combine(AppContext.BaseDirectory, \"bin\")"));
        });
    }

    [Test]
    public void BundledServerPluginsTargetTheHostFramework()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] incompatibleProjects = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "Vega.Modules.*.csproj", SearchOption.AllDirectories)
            .Where(path => !File.ReadAllText(path).Contains("<TargetFramework>net11.0</TargetFramework>", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.That(
            incompatibleProjects,
            Is.Empty,
            $"Bundled ServerPlugins must target the net11.0 host. Offenders: {string.Join(", ", incompatibleProjects)}");
    }

    [Test]
    public void NuGetPublicationProducesOneUnifiedPackageWithVisibleMonoModOptIn()
    {
        string repositoryRoot = FindRepositoryRoot();
        string defaults = File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Build.props"));
        string project = File.ReadAllText(Path.Combine(
            repositoryRoot, "packaging", "Vega.Package", "Vega.Package.csproj"));
        string packageProps = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "packaging",
            "Vega.Package",
            "buildTransitive",
            "VegaKernel.props"));
        string workflow = File.ReadAllText(Path.Combine(
            repositoryRoot, ".github", "workflows", "release-tag.yml"));
        string nuspec = File.ReadAllText(Path.Combine(
            repositoryRoot, "packaging", "Vega.Package", "Vega.Package.nuspec"));

        Assert.Multiple(() =>
        {
            Assert.That(defaults, Does.Contain("<IsPackable Condition=\"'$(IsPackable)' == ''\">false"));
            Assert.That(project, Does.Contain("<PackageId>VegaKernel</PackageId>"));
            Assert.That(project, Does.Contain("<IsPackable>true</IsPackable>"));
            Assert.That(project, Does.Contain("PackagePath=\"buildTransitive\\VegaKernel.props\""));
            Assert.That(packageProps, Does.Contain("<MonoMod_ReallySkipCheckTargetRuntime>true"));
            Assert.That(packageProps, Does.Contain("Remove when the resolved MonoMod stack officially supports net11.0"));
            Assert.That(workflow, Does.Contain("dotnet pack packaging/Vega.Package/Vega.Package.csproj"));
            Assert.That(workflow, Does.Not.Contain("foreach ($project in $projects)"));
            Assert.That(workflow, Does.Not.Contain("artifacts/nuget/*.snupkg"));
            Assert.That(Regex.Matches(workflow, "dotnet pack").Count, Is.EqualTo(1));
            Assert.That(Regex.Matches(nuspec, @"\.dll"" target=""lib\\\$tfm\$""").Count, Is.EqualTo(17));
            Assert.That(Regex.Matches(nuspec, @"\.pdb"" target=""lib\\\$tfm\$""").Count, Is.EqualTo(17));
            Assert.That(nuspec, Does.Contain("Vega.Contracts.dll"));
            Assert.That(nuspec, Does.Contain("Vega.Contracts.pdb"));
            Assert.That(nuspec, Does.Not.Contain("<dependency id=\"VegaKernel."));
        });
    }

    [Test]
    public void CoreClrServerRetainsDynamicInProcessRuntimeBoundaries()
    {
        string repositoryRoot = FindRepositoryRoot();
        string runtimeProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Runtime", "Vega.Runtime.csproj"));
        string runtime = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Runtime", "Program.cs"));
        string packetHook = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Terraria", "Hooking", "NativePlayerPacketSecurityHooks.cs"));
        string packetAdapter = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Terraria", "Networking", "MultiplicityPacketProcessing.cs"));
        string[] productExecutables = Directory
            .EnumerateFiles(Path.Combine(repositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains("<OutputType>Exe</OutputType>", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(productExecutables, Is.EqualTo(new[] { "src/Vega.Runtime/Vega.Runtime.csproj" }));
            Assert.That(runtimeProject, Does.Contain("<PublishAot>false</PublishAot>"));
            Assert.That(runtimeProject, Does.Contain("<TieredCompilation>true</TieredCompilation>"));
            Assert.That(runtimeProject, Does.Contain("<TieredPGO>true</TieredPGO>"));
            Assert.That(runtimeProject, Does.Contain("RejectNativeAotServerPublish"));
            Assert.That(runtime, Does.Contain("new ServerPluginHotReloadHost("));
            Assert.That(runtime, Does.Contain("new SoftRestartCoordinator("));
            Assert.That(packetHook, Does.Contain("MultiplicityPacketProcessing.ProcessInboundPayload("));
            Assert.That(packetAdapter, Does.Contain("processor.Process(in context)"));
            Assert.That(packetAdapter, Does.Not.Contain("ProcessStartInfo"));
            Assert.That(packetAdapter, Does.Not.Contain("Task.Run"));
        });
    }

    [Test]
    public void ContractsIsAnAotCompatibleIsolatedBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        string contractsDirectory = Path.Combine(repositoryRoot, "src", "Vega.Contracts");
        string project = File.ReadAllText(Path.Combine(contractsDirectory, "Vega.Contracts.csproj"));
        string[] forbiddenTokens =
        [
            "System.Reflection",
            "AssemblyLoadContext",
            "Activator.CreateInstance",
            "System.Reflection.Emit",
            "Terraria.",
            "OTAPI",
            "Terminal.Gui"
        ];
        string[] offenders = Directory
            .EnumerateFiles(contractsDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => forbiddenTokens.Any(token =>
                File.ReadAllText(path).Contains(token, StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(repositoryRoot, path))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("<IsAotCompatible>true</IsAotCompatible>"));
            Assert.That(project, Does.Not.Contain("<ProjectReference"));
            Assert.That(project, Does.Not.Contain("<PackageReference"));
            Assert.That(offenders, Is.Empty,
                $"AOT-compatible contracts must remain isolated. Offenders: {string.Join(", ", offenders)}");
        });
    }

    [Test]
    public void NativeGameThreadServicesContinueTickingWithoutConnectedPlayers()
    {
        string repositoryRoot = FindRepositoryRoot();
        string[] adapters =
        [
            Path.Combine(repositoryRoot, "src", "Vega.Terraria", "Operations", "TerrariaOperationsState.cs"),
            Path.Combine(repositoryRoot, "src", "Vega.Terraria", "Players", "TerrariaPlayerBoundary.cs"),
            Path.Combine(repositoryRoot, "src", "Vega.Terraria", "Threading", "OtapiGameThreadDispatcher.cs"),
            Path.Combine(repositoryRoot, "src", "Vega.Terraria", "Worlds", "TerrariaWorldClockSynchronizer.cs")
        ];

        Assert.Multiple(() =>
        {
            foreach (string adapter in adapters)
            {
                string source = File.ReadAllText(adapter);
                Assert.That(source, Does.Contain("HookEvents.Terraria.Main.Update"), adapter);
                Assert.That(source, Does.Contain("OnTickForThirdPartySoftwareOnly"), adapter);
                Assert.That(source, Does.Contain("!global::Terraria.Netplay.HasFullyConnectedClients"), adapter);
            }
        });
    }

    [Test]
    public void PublishedRuntimeDeclaresAssembliesRequiredByOtapiTypePreloading()
    {
        var repositoryRoot = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "Vega.Runtime", "Vega.Runtime.csproj"));

        Assert.Multiple(() =>
        {
            Assert.That(project, Does.Contain("PackageReference Include=\"System.Configuration.ConfigurationManager\""));
            Assert.That(project, Does.Contain("PackageReference Include=\"System.Drawing.Common\""));
        });
    }

    [Test]
    public void Vega_native_packet_security_owns_migrated_player_identity_packets()
    {
        var repositoryRoot = FindRepositoryRoot();
        var hook = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Hooking",
            "NativePlayerPacketSecurityHooks.cs"));
        var classification = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Networking",
            "PacketPipelineClassification.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(classification, Does.Contain("PacketTypes.PlayerHp"));
            Assert.That(classification, Does.Contain("PacketTypes.PlayerMana"));
            Assert.That(classification, Does.Contain("PacketTypes.PlayerTeam"));
            Assert.That(classification, Does.Contain("PacketTypes.TogglePvp"));
            Assert.That(hook, Does.Contain("PacketPipelineClassification.Get(packetId).SecurityProcessed"));
            Assert.That(hook, Does.Not.Contain("GetDataHandlers"));
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
    public void PacketPipelineClassificationRemainsInternalAndPreservesSynchronousMultiplicityIngress()
    {
        string repositoryRoot = FindRepositoryRoot();
        string classification = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Networking",
            "PacketPipelineClassification.cs"));
        string ingress = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Networking",
            "MultiplicityPacketProcessing.cs"));
        string hook = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Terraria",
            "Hooking",
            "NativePlayerPacketSecurityHooks.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(classification, Does.Contain("internal static class PacketPipelineClassification"));
            Assert.That(classification, Does.Contain("PacketPreparationClass.GameThreadOnly"));
            Assert.That(classification, Does.Contain("PacketPreparationClass.PartiallyOffloadable"));
            Assert.That(ingress, Does.Contain("PacketViewParser.TryParsePayloadBounded"));
            Assert.That(ingress, Does.Contain("processor.Process(in context)"));
            Assert.That(hook, Does.Not.Contain("Task.Run"));
            Assert.That(hook, Does.Not.Contain("Channel<"));
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
        string cleanerProject = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "Vega.Modules.AutoWorldCleaner",
            "Vega.Modules.AutoWorldCleaner.csproj"));
        string cleanerSource = string.Join('\n', Directory
            .EnumerateFiles(
                Path.Combine(repositoryRoot, "src", "Vega.Modules.AutoWorldCleaner"),
                "*.cs",
                SearchOption.AllDirectories)
            .Select(File.ReadAllText));

        Assert.Multiple(() =>
        {
            Assert.That(customItems, Does.Contain("Multiplicity.Packets"));
            Assert.That(customItems, Does.Contain("TweakItem"));
            Assert.That(customItems, Does.Not.Contain("PacketWriter"));
            Assert.That(pvp, Does.Contain("IPacketProcessor<PacketView>"));
            Assert.That(pvp, Does.Contain("IPlayerCombatDirectory"));
            Assert.That(pvp, Does.Not.Contain("TSPlayer"));
            Assert.That(cleanerProject, Does.Contain("Vega.Contracts"));
            Assert.That(cleanerProject, Does.Not.Contain("Vega.Terraria"));
            Assert.That(cleanerProject, Does.Not.Contain("Vega.Core"));
            Assert.That(cleanerSource, Does.Contain("IWorldEntityMaintenance"));
            Assert.That(cleanerSource, Does.Not.Contain("Terraria.Main"));
            Assert.That(cleanerSource, Does.Not.Contain("Terraria.Projectile"));
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
            Assert.That(consumers.Any(path => path.Replace('\\', '/').Contains("Vega.Core/Extensions", StringComparison.Ordinal)), Is.True);
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
