using Multiplicity.Packets.Views;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Commands;
using Vega.Contracts.Events;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;
using Vega.Contracts.Networking;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Runtime;
using Vega.Contracts.Scenes;
using Vega.Contracts.Worlds;
using Vega.Terraria.Networking;
using Vega.Terraria.Operations;
using Vega.Terraria.Players;
using Vega.Terraria.Threading;
using Vega.Terraria.Worlds;

namespace Vega.Terraria;

public sealed record TerrariaServerHostServices(
    TerrariaServerHostServices.RuntimeServices Runtime,
    TerrariaServerHostServices.WorldServices World,
    TerrariaServerHostServices.NetworkServices Network,
    TerrariaServerHostServices.PlayerServices Players,
    TerrariaServerHostServices.CommandServices Commands,
    TerrariaServerHostServices.SceneServices Scenes,
    TerrariaServerHostServices.CharacterServices Characters)
{
    public sealed record RuntimeServices(
        IRuntimeLifecycleController Lifecycle,
        TerrariaRuntimeGameThreadDispatcher GameThread,
        TerrariaOperationsState Operations,
        IReadOnlyList<IRuntimeStartupParticipant> StartupParticipants,
        IOperationsLogger Logger);

    public sealed record WorldServices(
        WorldId Id,
        IEventPipeline<WorldHardmodeStarting> HardmodeStarting,
        TerrariaWorldImageService Images,
        TerrariaWorldMaintenanceService Maintenance,
        IWorldClockController Clock,
        IChestInteractionPolicy ChestPolicy,
        IWorldGameplayEvents? Events = null);

    public sealed record NetworkServices(
        TerrariaConnectionMetadataRegistry Connections,
        IPacketProcessor<PacketView> PacketPolicies,
        INetworkMetricsRecorder Metrics,
        ISecuritySignalRecorder SecuritySignals,
        ServerNetworkOptions? Options = null,
        IConnectionAdmissionPolicy? Admission = null);

    public sealed record PlayerServices(
        TerrariaPlayerBoundary Boundary,
        IPlayerAuthorizationService Authorization,
        IPlayerAccountSessionService Accounts);

    public sealed record CommandServices(
        ICommandDispatcher Dispatcher,
        IChatPipeline Chat,
        ILocalizationService Localization,
        IPlayerCultureService Cultures);

    public sealed record SceneServices(
        ISceneRuntime Runtime,
        ISceneVisibilityRuntime Visibility,
        ISceneRenderSink Renderer);

    public sealed record CharacterServices(
        bool Authoritative = false,
        Func<PlayerSnapshot, bool>? GameplayReadiness = null,
        TerrariaSandboxSscCharacterAdapter? EarlyJoin = null,
        IPacketProcessor<PacketView>? JoinInventoryPolicy = null);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Runtime);
        ArgumentNullException.ThrowIfNull(Runtime.Lifecycle);
        ArgumentNullException.ThrowIfNull(Runtime.GameThread);
        ArgumentNullException.ThrowIfNull(Runtime.Operations);
        ArgumentNullException.ThrowIfNull(Runtime.StartupParticipants);
        ArgumentNullException.ThrowIfNull(Runtime.Logger);

        ArgumentNullException.ThrowIfNull(World);
        ArgumentNullException.ThrowIfNull(World.HardmodeStarting);
        ArgumentNullException.ThrowIfNull(World.Images);
        ArgumentNullException.ThrowIfNull(World.Maintenance);
        ArgumentNullException.ThrowIfNull(World.Clock);
        ArgumentNullException.ThrowIfNull(World.ChestPolicy);

        ArgumentNullException.ThrowIfNull(Network);
        ArgumentNullException.ThrowIfNull(Network.Connections);
        ArgumentNullException.ThrowIfNull(Network.PacketPolicies);
        ArgumentNullException.ThrowIfNull(Network.Metrics);
        ArgumentNullException.ThrowIfNull(Network.SecuritySignals);

        ArgumentNullException.ThrowIfNull(Players);
        ArgumentNullException.ThrowIfNull(Players.Boundary);
        ArgumentNullException.ThrowIfNull(Players.Authorization);
        ArgumentNullException.ThrowIfNull(Players.Accounts);

        ArgumentNullException.ThrowIfNull(Commands);
        ArgumentNullException.ThrowIfNull(Commands.Dispatcher);
        ArgumentNullException.ThrowIfNull(Commands.Chat);
        ArgumentNullException.ThrowIfNull(Commands.Localization);
        ArgumentNullException.ThrowIfNull(Commands.Cultures);

        ArgumentNullException.ThrowIfNull(Scenes);
        ArgumentNullException.ThrowIfNull(Scenes.Runtime);
        ArgumentNullException.ThrowIfNull(Scenes.Visibility);
        ArgumentNullException.ThrowIfNull(Scenes.Renderer);

        ArgumentNullException.ThrowIfNull(Characters);
        if (Characters.Authoritative && (Characters.GameplayReadiness is null || Characters.EarlyJoin is null))
            throw new InvalidOperationException("Authoritative character mode requires native character lifecycle and readiness.");
    }
}
