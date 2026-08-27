using System.Collections.Frozen;
using Vega.Contracts.Operations;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Regions;
using Vega.Contracts.Bans;
using Vega.Contracts.Moderation;
using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Worlds;
using Vega.Contracts.Scenes;
using Vega.Contracts.Backups;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;

namespace Vega.Operations.ReadModels;

public sealed class OperationsSnapshotProvider(
    string serverName,
    string version,
    DateTimeOffset startedAt,
    IRuntimeLifecycle lifecycle,
    IPlayerDirectory players,
    IServerRuntimeStateReader serverState,
    IModuleOperationsCatalog modules,
    IOperationsLogStore logs,
    IRuntimeMetricsReader runtimeMetrics,
    INetworkMetricsReader networkMetrics,
    IWorldLoadMetricsReader? worldLoadMetrics = null,
    IModuleDirectoryStatusReader? moduleDirectory = null,
    IWorldClockReader? worldClocks = null,
    IWorldCleanupStatusReader? worldCleanup = null,
    IWorldContextOperationsCatalog? worldContexts = null,
    ISecuritySignalReader? securitySignals = null,
    IRegionService? regions = null,
    IBanService? bans = null,
    IMuteService? mutes = null,
    ISceneRuntime? scenes = null,
    ISceneVisibilityRuntime? visibility = null,
    IDeploymentBackupService? backups = null,
    IAccountService? accounts = null,
    IAuthorizationService? authorization = null,
    IAccountIdentityEvidenceService? accountIdentities = null) : IOperationsSnapshotProvider
{
    private static readonly TimeSpan AdministrationCacheDuration = TimeSpan.FromSeconds(3);
    private readonly object administrationSync = new();
    private AdministrationOperationsSnapshot redactedAdministration = AdministrationOperationsSnapshot.Empty;
    private AdministrationOperationsSnapshot sensitiveAdministration = AdministrationOperationsSnapshot.Empty;
    private DateTimeOffset redactedAdministrationValidUntil = DateTimeOffset.MinValue;
    private DateTimeOffset sensitiveAdministrationValidUntil = DateTimeOffset.MinValue;

    public OperationsSnapshot Capture(
        OperationsLogFilter? logFilter = null,
        OperationsSnapshotExposure exposure = OperationsSnapshotExposure.Redacted)
    {
        ServerRuntimeStateSnapshot state = serverState.Read();
        PlayerSnapshot[] playerSnapshots = players.SnapshotOnlinePlayers()
            .Select(player => exposure == OperationsSnapshotExposure.Sensitive
                ? player
                : PlayerSnapshotPrivacy.RedactSensitive(player))
            .ToArray();
        ModuleOperationsSnapshot[] moduleSnapshots = modules
            .SnapshotModules()
            .Select(static module => module with
            {
                Dependencies = Array.AsReadOnly(module.Dependencies.ToArray())
            })
            .ToArray();
        ModuleDirectoryStatusSnapshot moduleDirectoryStatus =
            moduleDirectory?.Read() ?? ModuleDirectoryStatusSnapshot.Unmonitored;
        WorldCleanupStatusSnapshot worldCleanupStatus =
            worldCleanup?.Read() ?? WorldCleanupStatusSnapshot.Disabled;
        DeploymentBackupSnapshot backupStatus = backups?.Read() ?? DeploymentBackupSnapshot.Disabled;
        string[] warnings = BuildWarnings(
            state,
            moduleSnapshots,
            moduleDirectoryStatus,
            worldCleanupStatus,
            backupStatus);
        RuntimeLifecycleStage stage = lifecycle.CurrentStage;
        WorldContextSnapshot[] contexts = worldContexts?.SnapshotContexts().ToArray() ??
        [
            new WorldContextSnapshot(
                WorldId.Primary,
                state.WorldName ?? "Primary world",
                WorldContextKind.Primary,
                state.IsWorldReady ? WorldContextSimulationState.Running : WorldContextSimulationState.Stopped,
                Array.AsReadOnly(playerSnapshots.Select(static player => player.Id).ToArray()),
                0,
                "primary",
                TimeSpan.Zero)
        ];
        RegionSnapshot[] regionSnapshots = contexts
            .SelectMany(context => regions?.SnapshotRegions(context.Id) ?? [])
            .OrderBy(static region => region.WorldId.Value, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(static region => region.Priority)
            .ThenBy(static region => region.Name, StringComparer.OrdinalIgnoreCase)
            .Take(1_000)
            .Select(static region => region with
            {
                Members = region.Members.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                Groups = region.Groups.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                Flags = region.Flags.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            })
            .ToArray();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BanSnapshot[] banSnapshots = bans?.SnapshotActive(now)
            .Take(200)
            .Select(ban => exposure == OperationsSnapshotExposure.Sensitive
                ? ban
                : ban with { Subject = Redact(ban.Subject) })
            .ToArray() ?? [];
        MuteSnapshot[] muteSnapshots = mutes?.SnapshotActive(now).Take(200).ToArray() ?? [];

        TickRateStatisticsSnapshot tickRate = state.TickRate.SampleCount > 0
            ? state.TickRate
            : new TickRateStatisticsSnapshot(
                state.TicksPerSecond,
                state.TicksPerSecond,
                state.TicksPerSecond,
                state.SampledAt,
                state.TicksPerSecond,
                state.SampledAt,
                state.TicksPerSecond > 0 ? 1 : 0);
        var dashboard = new ServerDashboardSnapshot(
            serverName,
            version,
            startedAt,
            lifecycle.Elapsed,
            stage,
            state.WorldName,
            state.IsWorldReady,
            stage >= RuntimeLifecycleStage.NetworkReady,
            playerSnapshots.Length,
            state.MaximumPlayers,
            state.ListenerPort,
            state.TicksPerSecond,
            Array.AsReadOnly(warnings))
        {
            TickRate = tickRate
        };

        return new OperationsSnapshot(
            dashboard,
            Array.AsReadOnly(playerSnapshots),
            Array.AsReadOnly(moduleSnapshots),
            logs.Query(logFilter ?? new OperationsLogFilter()),
            runtimeMetrics.Read(),
            networkMetrics.Read())
        {
            WorldLoad = worldLoadMetrics?.Read(),
            ModuleDirectory = moduleDirectoryStatus,
            WorldClocks = Array.AsReadOnly(worldClocks?.SnapshotWorldClocks().ToArray() ?? []),
            WorldCleanup = worldCleanupStatus,
            Chat = Array.AsReadOnly(logs
                .Query(new OperationsLogFilter(Source: "chat", Limit: 200))
                .Where(static record => string.Equals(record.Source, "chat", StringComparison.OrdinalIgnoreCase))
                .ToArray()),
            WorldContexts = Array.AsReadOnly(contexts),
            CanMovePlayersBetweenWorlds = worldContexts?.CanMovePlayers == true,
            SecuritySignals = securitySignals?.Read(
                exposure == OperationsSnapshotExposure.Sensitive
                    ? SecuritySignalExposure.Sensitive
                    : SecuritySignalExposure.Redacted) ??
                Array.Empty<SecuritySignal>(),
            Regions = Array.AsReadOnly(regionSnapshots),
            Bans = Array.AsReadOnly(banSnapshots),
            Mutes = Array.AsReadOnly(muteSnapshots),
            Scenes = scenes?.SnapshotScenes() ?? Array.Empty<SceneSnapshot>(),
            VisibilityLayers = visibility?.SnapshotLayers() ?? Array.Empty<VisibilityLayerSnapshot>(),
            Backup = backupStatus,
            Administration = ReadAdministration(exposure, now)
        };
    }

    private AdministrationOperationsSnapshot ReadAdministration(
        OperationsSnapshotExposure exposure,
        DateTimeOffset now)
    {
        if (accounts is null || authorization is null)
        {
            return AdministrationOperationsSnapshot.Empty;
        }

        lock (administrationSync)
        {
            if (exposure == OperationsSnapshotExposure.Sensitive)
            {
                if (now < sensitiveAdministrationValidUntil)
                {
                    return sensitiveAdministration;
                }

                sensitiveAdministration = CaptureAdministration(includePossibleAccounts: true, now);
                sensitiveAdministrationValidUntil = now + AdministrationCacheDuration;
                return sensitiveAdministration;
            }

            if (now < redactedAdministrationValidUntil)
            {
                return redactedAdministration;
            }

            redactedAdministration = CaptureAdministration(includePossibleAccounts: false, now);
            redactedAdministrationValidUntil = now + AdministrationCacheDuration;
            return redactedAdministration;
        }
    }

    private AdministrationOperationsSnapshot CaptureAdministration(
        bool includePossibleAccounts,
        DateTimeOffset sampledAt)
    {
        AccountSnapshot[] accountSnapshots = accounts!.SnapshotAccounts().ToArray();
        GroupSnapshot[] groupSnapshots = authorization!.SnapshotGroups().ToArray();
        IReadOnlyDictionary<AccountId, IReadOnlyList<PossibleAccountOperationsSnapshot>> possible =
            includePossibleAccounts && accountIdentities is not null
                ? BuildPossibleAccountLinks(accountIdentities.SnapshotEvidence())
                : new Dictionary<AccountId, IReadOnlyList<PossibleAccountOperationsSnapshot>>();

        Dictionary<string, int> accountCounts = accountSnapshots
            .GroupBy(static account => account.GroupName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        PermissionGroupOperationsSnapshot[] groups = groupSnapshots
            .OrderBy(static group => group.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PermissionGroupOperationsSnapshot(
                group.Name,
                group.ParentName,
                Array.AsReadOnly(group.Permissions.OrderBy(static permission => permission, StringComparer.OrdinalIgnoreCase).ToArray()),
                Array.AsReadOnly(group.DeniedPermissions.OrderBy(static permission => permission, StringComparer.OrdinalIgnoreCase).ToArray()),
                accountCounts.GetValueOrDefault(group.Name),
                group.Revision))
            .ToArray();
        AccountOperationsSnapshot[] projectedAccounts = accountSnapshots
            .OrderBy(static account => account.Name, StringComparer.OrdinalIgnoreCase)
            .Select(account => new AccountOperationsSnapshot(
                account.Id,
                account.Name,
                account.GroupName,
                account.LastSeenUtc,
                account.Culture,
                possible.GetValueOrDefault(account.Id) ?? Array.Empty<PossibleAccountOperationsSnapshot>()))
            .ToArray();
        return new AdministrationOperationsSnapshot(
            Array.AsReadOnly(groups),
            Array.AsReadOnly(projectedAccounts),
            sampledAt);
    }

    private static IReadOnlyDictionary<AccountId, IReadOnlyList<PossibleAccountOperationsSnapshot>> BuildPossibleAccountLinks(
        IReadOnlyList<AccountIdentityEvidenceSnapshot> evidence)
    {
        var links = new Dictionary<(AccountId Source, AccountId Target), LinkAccumulator>();
        foreach (IGrouping<(AccountIdentityEvidenceKind Kind, string Value), AccountIdentityEvidenceSnapshot> signal in evidence
                     .GroupBy(static item => (item.Kind, item.Value)))
        {
            AccountIdentityEvidenceSnapshot[] members = signal
                .GroupBy(static item => item.AccountId)
                .Select(static group => group.OrderByDescending(item => item.LastSeenAt).First())
                .ToArray();
            if (members.Length < 2)
            {
                continue;
            }

            foreach (AccountIdentityEvidenceSnapshot source in members)
            {
                foreach (AccountIdentityEvidenceSnapshot target in members)
                {
                    if (source.AccountId == target.AccountId)
                    {
                        continue;
                    }

                    var key = (source.AccountId, target.AccountId);
                    if (!links.TryGetValue(key, out LinkAccumulator? accumulator))
                    {
                        accumulator = new LinkAccumulator(target.AccountName);
                        links.Add(key, accumulator);
                    }

                    accumulator.SharedSignals++;
                    accumulator.SharedClientUuid |= signal.Key.Kind == AccountIdentityEvidenceKind.ClientUuid;
                    accumulator.SharedIpAddress |= signal.Key.Kind == AccountIdentityEvidenceKind.IpAddress;
                    DateTimeOffset matchedAt = source.LastSeenAt <= target.LastSeenAt
                        ? source.LastSeenAt
                        : target.LastSeenAt;
                    if (matchedAt > accumulator.LastMatchedAt)
                    {
                        accumulator.LastMatchedAt = matchedAt;
                    }
                }
            }
        }

        return links
            .GroupBy(static pair => pair.Key.Source)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<PossibleAccountOperationsSnapshot>)Array.AsReadOnly(group
                    .Select(pair => new PossibleAccountOperationsSnapshot(
                        pair.Value.AccountName,
                        pair.Value.SharedSignals,
                        pair.Value.SharedClientUuid,
                        pair.Value.SharedIpAddress,
                        pair.Value.LastMatchedAt))
                    .OrderByDescending(static item => item.SharedClientUuid)
                    .ThenByDescending(static item => item.SharedSignals)
                    .ThenByDescending(static item => item.LastMatchedAt)
                    .ThenBy(static item => item.AccountName, StringComparer.OrdinalIgnoreCase)
                    .ToArray()));
    }

    private static GlobalBanSubject Redact(GlobalBanSubject subject) =>
        subject.Kind is GlobalBanSubjectKind.Uuid or GlobalBanSubjectKind.IpAddress or GlobalBanSubjectKind.Network
            ? subject with { Value = "<redacted>" }
            : subject;

    private static string[] BuildWarnings(
        ServerRuntimeStateSnapshot state,
        IReadOnlyList<ModuleOperationsSnapshot> modules,
        ModuleDirectoryStatusSnapshot moduleDirectory,
        WorldCleanupStatusSnapshot worldCleanup,
        DeploymentBackupSnapshot backup)
    {
        var warnings = new List<string>();
        if (DateTimeOffset.UtcNow - state.SampledAt > TimeSpan.FromSeconds(5))
        {
            warnings.Add("Terraria runtime snapshot is stale.");
        }

        if (state.TickRate.SampleCount > 0 && state.TickRate.Current < 15)
        {
            warnings.Add($"Critical tick rate: {state.TickRate.Current:F1} TPS.");
        }

        if (moduleDirectory.IsRestartRequired)
        {
            warnings.Add("ServerPlugins change could not be hot-applied; restart required.");
        }

        if (moduleDirectory.Failure is not null)
        {
            warnings.Add($"ServerPlugins hot-reload issue: {moduleDirectory.Failure}");
        }

        if (worldCleanup.State == WorldCleanupRunState.Failed)
        {
            warnings.Add($"AutoWorldCleaner failed: {worldCleanup.Detail ?? "unknown"}.");
        }

        if (backup.State == DeploymentBackupState.Failed)
        {
            warnings.Add($"Deployment backup failed: {backup.Detail ?? "unknown"}.");
        }

        foreach (ModuleOperationsSnapshot module in modules)
        {
            if (module.State is ModuleLoadState.Degraded or ModuleLoadState.Failed)
            {
                warnings.Add($"Module {module.Name} is {module.State}: {module.Failure ?? "no details"}");
            }
        }

        return warnings.ToArray();
    }

    private sealed class LinkAccumulator(string accountName)
    {
        public string AccountName { get; } = accountName;

        public int SharedSignals { get; set; }

        public bool SharedClientUuid { get; set; }

        public bool SharedIpAddress { get; set; }

        public DateTimeOffset LastMatchedAt { get; set; } = DateTimeOffset.MinValue;
    }
}

public sealed class StaticModuleOperationsCatalog : IModuleOperationsCatalog
{
    private readonly IReadOnlyList<ModuleOperationsSnapshot> modules;

    public StaticModuleOperationsCatalog(IEnumerable<ModuleOperationsSnapshot> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        this.modules = Array.AsReadOnly(modules
            .Select(static module => module with
            {
                Dependencies = Array.AsReadOnly(module.Dependencies.ToArray())
            })
            .ToArray());
    }

    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => modules;
}

public sealed class CompositeModuleOperationsCatalog(params IModuleOperationsCatalog[] catalogs) : IModuleOperationsCatalog
{
    private readonly IModuleOperationsCatalog[] catalogs = catalogs ??
        throw new ArgumentNullException(nameof(catalogs));

    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => Array.AsReadOnly(catalogs
        .SelectMany(static catalog => catalog.SnapshotModules())
        .ToArray());
}
