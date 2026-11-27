using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Cluster;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Modules;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;

namespace Vega.Core.Modules;

/// <summary>Capabilities supplied by one server process to successive plugin activations.</summary>
public sealed record ServerPluginHostServices(ICommandRegistry Commands, IOperationsLogger Logger)
{
    public PlayerServices Players { get; init; } = new();
    public WorldServices World { get; init; } = new();
    public IdentityServices Identity { get; init; } = new();
    public ExtensionServices Extensions { get; init; } = new();
    public IInboundPacketRateReader? PacketRates { get; init; }

    public sealed record PlayerServices(IPlayerDirectory? Directory = null, IPlayerMessenger? Messenger = null,
        IPlayerOperations? Operations = null, IPlayerSessionLifecycle? Lifecycle = null, IGameThreadDispatcher? GameThread = null);

    public sealed record WorldServices(WorldId? Id = null, IWorldGameplayEvents? Events = null,
        IPublicChatObservations? Chat = null, IWorldPublicWarpReader? Warps = null, IPlayerPlaytimeSessionReader? Playtime = null);

    public sealed record IdentityServices(IPlayerAccountSessionService? Sessions = null, IAccountService? Accounts = null,
        IAuthorizationService? Groups = null, IPlayerAuthorizationService? Permissions = null, IRoleLeaseService? RoleLeases = null);

    public sealed record ExtensionServices(IPluginDocumentStore? Documents = null, PluginNpcEconomyRegistry? NpcEconomy = null,
        PluginRestRegistry? Rest = null, PluginClusterRegistry? Cluster = null, IClusterNotificationSender? ClusterSender = null);

    internal HotModuleRegistrationScope CreateScope(string name, Guid activationId, string dataRoot, string? configurationRoot,
        PluginServiceRegistry registry, ServerPluginHotReloadOptions options, Action onQuarantine) =>
        new(Commands, Logger, name, Path.Combine(dataRoot, HotModuleRegistrationScope.SafeDirectoryName(name)),
            Players.Directory, Players.Messenger, Players.Operations, Players.Lifecycle, Players.GameThread,
            pluginConfigurationRoot: configurationRoot, activationId: activationId,
            faultThreshold: options.FaultThreshold, faultWindow: TimeSpan.FromSeconds(options.FaultWindowSeconds),
            onQuarantine: onQuarantine, worldEvents: World.Events, activityWorldId: World.Id, publicChat: World.Chat,
            warps: World.Warps, playtime: World.Playtime, authentication: Identity.Sessions, accounts: Identity.Accounts,
            groups: Identity.Groups, playerPermissions: Identity.Permissions, roleLeases: Identity.RoleLeases,
            identityAssignmentGroups: options.IdentityGroupAssignments.GetValueOrDefault(name), serviceRegistry: registry,
            documentStore: Extensions.Documents, npcEconomyRegistry: Extensions.NpcEconomy, pluginRestRegistry: Extensions.Rest,
            pluginClusterRegistry: Extensions.Cluster, clusterSender: Extensions.ClusterSender, packetRates: PacketRates);
}
