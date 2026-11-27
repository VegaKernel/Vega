using Vega.Contracts.Sandboxes;
using Multiplicity.Packets.Views;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Items;
using Vega.Core.Authorization;
using Vega.Core.Bans;
using Vega.Core.Commands;
using Vega.Core.Commands.Administration;
using Vega.Core.Commands.Players;
using Vega.Core.Messaging;
using Vega.Core.Modules;
using Vega.Core.Networking;
using Vega.Core.Players;
using Vega.Modules.Administration;
using Vega.Modules.Navigation;
using Vega.Modules.Restrictions;
using Vega.Persistence;
using Vega.Regions;
using Vega.Sandboxes;
using Vega.Terraria.Networking;
using Vega.Terraria.Players;
using Vega.Terraria.Worlds;

namespace Vega.Runtime;

internal static partial class RuntimeHost
{
    private static Identity? CreateIdentity(RuntimeResourceScope resources, Foundation foundation, World world)
    {
        var playerBoundary = foundation.PlayerBoundary;
        var commandDispatcher = foundation.CommandDispatcher;
        var worldEnvironment = world.WorldEnvironment;
        IVegaDatabase database;
        try
        {
            VegaDatabaseOptions databaseOptions = VegaDatabaseOptionsStore.Load(
                Path.Combine(AppContext.BaseDirectory, "config", "database.json"));
            database = VegaDatabaseFactory.Create(databaseOptions, AppContext.BaseDirectory);
            world.RegionReset.Bind(new RegionWorldResetParticipant(database.Regions));
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            foundation.Logger.Log(
                OperationsLogLevel.Critical,
                "persistence",
                "Vega database configuration is invalid.",
                ex);
            return null;
        }

        var banService = new GlobalBanService(database.GlobalBans);
        var accountSessions = resources.Own(new PlayerAccountSessionService(
            database.Accounts,
            database.Authorization,
            foundation.PlayerAuthorization,
            foundation.Localization,
            playerBoundary,
            banService,
            database.RoleLeases));
        var playtime = resources.Own(new PlayerPlaytimeService(playerBoundary, playerBoundary, playerBoundary,
            accountSessions, database.Playtime, foundation.RuntimeWorldId, foundation.Logger));
        resources.Own(commandDispatcher.Register(new HomeCommand(HomeCommandAction.Set,
            playerBoundary, playerBoundary, worldEnvironment, accountSessions, database.Authorization, database.Homes)));
        resources.Own(commandDispatcher.Register(new HomeCommand(HomeCommandAction.Delete,
            playerBoundary, playerBoundary, worldEnvironment, accountSessions, database.Authorization, database.Homes)));
        resources.Own(commandDispatcher.Register(new HomeCommand(HomeCommandAction.Teleport,
            playerBoundary, playerBoundary, worldEnvironment, accountSessions, database.Authorization, database.Homes)));
        resources.Own(commandDispatcher.Register(new PlaytimeCommand(playerBoundary, playtime)));
        resources.Own(commandDispatcher.Register(new RocketCommand(playerBoundary,
            (player, token) => playerBoundary.RocketAsync(player, accountSessions, token))));
        resources.Own(commandDispatcher.Register(
            new RegisterAccountCommand(accountSessions)));
        resources.Own(commandDispatcher.Register(
            new LoginAccountCommand(accountSessions)));
        resources.Own(commandDispatcher.Register(
            new LogoutAccountCommand(accountSessions)));
        resources.Own(commandDispatcher.Register(
            new PasswordAccountCommand(accountSessions)));
        resources.Own(commandDispatcher.Register(
            new LanguageCommand(foundation.Localization, accountSessions)));
        var inventoryService = new TerrariaPlayerInventoryService(foundation.GameThreadDispatcher, playerBoundary);
        resources.Own(commandDispatcher.Register(
            new UserInfoCommand(playerBoundary, accountSessions, database.RoleLeases)));
        resources.Own(commandDispatcher.Register(
            new InventoryCommand(playerBoundary, inventoryService)));
        var chat = new ChatPipeline(
            moderation: [new MuteChatModerationRule(accountSessions, database.Mutes)],
            renderers: [foundation.ModuleRouter],
            presentation: player => accountSessions.TryGetBinding(player, out var binding) &&
                accountSessions.TryRunWithBinding(binding, () => foundation.PlayerAuthorization.GetChatPresentation(player.Id), out var style) ? style : null);
        resources.Own(new PlayerChatCommands(commandDispatcher, playerBoundary, playerBoundary, playerBoundary, chat));

        return new(database, banService, accountSessions, playtime, inventoryService, chat);
    }

    private static Characters? CreateCharacters(RuntimeResourceScope resources, Launch launch, Foundation foundation, Admission admission, World world, Identity identity)
    {
        var backendLaunch = launch.BackendLaunch;
        var runtimeWorldId = foundation.RuntimeWorldId;
        var playerBoundary = foundation.PlayerBoundary;
        var commandDispatcher = foundation.CommandDispatcher;
        var logger = foundation.Logger;
        GameplayEnforcementOptions gameplayOptions = GameplayEnforcementOptionsStore.Load(
            Path.Combine(AppContext.BaseDirectory, "config", "security.json"),
            logger);
        try
        {
            foundation.ConnectionMetadata.ConfigureDimensions(
                gameplayOptions.EnableDimensionsPacket,
                gameplayOptions.DimensionsTrustedProxies);
            foundation.ConnectionMetadata.ConfigureSandboxAdmission(
                admission.SandboxAdmissions,
                runtimeWorldId,
                backendLaunch.IsBackend);
        }
        catch (InvalidDataException ex)
        {
            logger.Log(OperationsLogLevel.Critical, "connection-metadata", "Dimensions trust configuration is invalid.", ex);
            return null;
        }
        PrimaryCharacterPersistenceOptions primaryCharacterOptions;
        try { primaryCharacterOptions = PrimaryCharacterPersistenceOptions.Load(Path.Combine(AppContext.BaseDirectory, "config", "primary-character-persistence.json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            logger.Log(OperationsLogLevel.Critical, "character-persistence", "Primary character persistence settings are invalid.", ex);
            return null;
        }
        bool authoritativeCharacters = backendLaunch.IsBackend
            ? backendLaunch.SscMode != SandboxSscMode.Disabled : primaryCharacterOptions.Authoritative;
        if (!backendLaunch.IsBackend && primaryCharacterOptions.Enabled && File.Exists(Path.Combine(foundation.RuntimeDataRoot, "primary-characters.json")))
        {
            logger.Log(OperationsLogLevel.Critical, "character-persistence",
                "A pre-release primary-characters.json exists. The release uses only the shared SSC store. Preserve the old file and export it with the matching development version if its data is needed.");
            return null;
        }
        var sandboxSscSessions = resources.Own(new SandboxSscSessionCoordinator(
            playerBoundary,
            playerBoundary,
            identity.AccountSessions,
            playerBoundary,
            admission.SandboxSscStore,
            admission.SandboxCharacterAdapter,
            runtimeWorldId,
            backendLaunch.IsBackend ? backendLaunch.SscMode : SandboxSscMode.Inherit,
            logger,
            primaryCharacterStore: !backendLaunch.IsBackend && primaryCharacterOptions.Enabled ? admission.SandboxSscStore : null));
        var npcAccountStore = new FileNpcAccountStore(Path.Combine(foundation.RuntimeDataRoot, "npc-account-progress"));
        var npcEconomy = new PluginNpcEconomyRegistry(npcAccountStore);
        var worldNpcActors = resources.Own(new TerrariaNpcActorService(foundation.GameThreadDispatcher, runtimeWorldId, playerBoundary, world.WorldEvents, logger,
            identity.AccountSessions, npcAccountStore, npcEconomy, authoritativeCharacters ? sandboxSscSessions.IsCharacterReady : null,
            sandboxSscSessions.CheckpointActorTransactionAsync));
        resources.Own(commandDispatcher.Register(new SaveSscCommand(sandboxSscSessions)));
        resources.Own(commandDispatcher.Register(new OverrideSscCommand(sandboxSscSessions, playerBoundary)));
        resources.Own(commandDispatcher.Register(new UploadSscCommand(sandboxSscSessions, playerBoundary)));

        return new(gameplayOptions, primaryCharacterOptions, authoritativeCharacters, sandboxSscSessions, npcEconomy, worldNpcActors);
    }

    private static PacketPolicies CreatePacketPolicies(RuntimeResourceScope resources, Foundation foundation, Admission admission, World world, Identity identity, Characters characters)
    {
        var runtimeWorldId = foundation.RuntimeWorldId;
        var playerBoundary = foundation.PlayerBoundary;
        var gameplayEnforcement = resources.Own(new GameplayEnforcementPolicy(
            characters.GameplayOptions,
            foundation.PlayerAuthorization,
            playerBoundary,
            playerBoundary));
        var packetRateLimits = resources.Own(new PacketRateLimitPolicy(playerBoundary));
        var inventoryPolicy = new PlayerInventorySanityPolicy(
            static () => TerrariaPacketSecurityLimits.CharacterSlotCount,
            static () => TerrariaPacketSecurityLimits.LoadoutCount,
            TerrariaPacketSecurityLimits.ItemTypeCount,
            TerrariaPacketSecurityLimits.PrefixCount,
            TerrariaPacketSecurityLimits.MaximumItemStack);
        var playerPacketPolicies = new PacketProcessorPipeline<PacketView>(
        [
            packetRateLimits,
            inventoryPolicy,
            new PlayerCharacterSyncPolicy(static () => TerrariaPacketSecurityLimits.BuffSlotCount, TerrariaPacketSecurityLimits.BuffTypeCount),
            gameplayEnforcement,
            new SceneManagedChestGuiPolicy(world.Scenes, runtimeWorldId, playerBoundary),
            new ChestInteractionAuthorityPolicy(playerBoundary),
            new TileEntityInteractionAuthorityPolicy(playerBoundary),
            new SceneInteractionAuthorityPolicy(world.Scenes, runtimeWorldId, playerBoundary),
            new SceneEntityInteractionAuthorityPolicy(world.Visibility, runtimeWorldId),
            new RegionBuildAuthorizationPolicy(
                identity.Database.Regions,
                playerBoundary,
                foundation.PlayerAuthorization,
                identity.AccountSessions,
                playerBoundary,
                world.BuildProtection),
            new NpcCoinPickupAuthorityPolicy(),
            new WorldItemSyncSanityPolicy(
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount),
            new ProjectileSpawnSanityPolicy(
                TerrariaPacketSecurityLimits.ProjectileCount,
                TerrariaPacketSecurityLimits.ProjectileTypeCount),
            new PlayerDeathDamageAuthorityPolicy(),
            new PlayerStateSanityPolicy(
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight,
                static () => TerrariaPacketSecurityLimits.InventorySlotCount),
            new WorldMutationSanityPolicy(
                static () => TerrariaPacketSecurityLimits.WorldWidth,
                static () => TerrariaPacketSecurityLimits.WorldHeight,
                static () => TerrariaPacketSecurityLimits.ChestCount,
                static () => TerrariaPacketSecurityLimits.QuickStackSlotCount,
                TerrariaPacketSecurityLimits.TileTypeCount,
                TerrariaPacketSecurityLimits.WallTypeCount,
                TerrariaPacketSecurityLimits.ItemTypeCount,
                TerrariaPacketSecurityLimits.PrefixCount,
                TerrariaPacketSecurityLimits.PaintColorCount),
            new PlayerPacketOwnershipPolicy(),
            admission.RestrictionPolicy,
            foundation.ModuleRouter,
            new PlayerGameplayIntentPolicy(playerBoundary, playerBoundary, world.WorldEvents, foundation.GameThreadDispatcher, runtimeWorldId,
                player => playerBoundary.TryGetSessionPlayer(player, out var native)
                    ? new CurrentPlayerIntentState(new PlayerPosition(native.position.X, native.position.Y), native.hostile, native.team) : null, foundation.Logger)
        ]);
        return new(inventoryPolicy, playerPacketPolicies);
    }

    private static void RegisterSandboxCommands(RuntimeResourceScope resources, Foundation foundation, Admission admission, Identity identity)
    {
        resources.Own(admission.SandboxControl is null
            ? null
            : foundation.CommandDispatcher.Register(new SandboxCommand(admission.SandboxControl, admission.SandboxRouting, foundation.Logger)));
        resources.Own(foundation.CommandDispatcher.Register(new SandboxPlayerTransferCommand(
            admission.SandboxDirectory,
            admission.SandboxRouting,
            new TerrariaSandboxPlayerTransfer(
                foundation.GameThreadDispatcher,
                foundation.ConnectionMetadata,
                admission.SandboxAdmissions,
                admission.SandboxSscStore,
                admission.SandboxCharacterAdapter,
                foundation.PlayerBoundary),
            foundation.PlayerBoundary,
            foundation.PlayerAuthorization,
            identity.AccountSessions)));
    }

    private static void RegisterAdministration(RuntimeResourceScope resources, Foundation foundation, Admission admission, World world, Identity identity)
    {
        var playerBoundary = foundation.PlayerBoundary;
        var commandDispatcher = foundation.CommandDispatcher;
        var restrictions = admission.Restrictions;
        var database = identity.Database;
        var accountSessions = identity.AccountSessions;
        resources.Own(commandDispatcher.Register(
            new BanCommand(playerBoundary, playerBoundary, identity.BanService, accountSessions: accountSessions, accounts: database.Accounts)));
        resources.Own(commandDispatcher.Register(
            new MuteCommand(playerBoundary, accountSessions, database.Mutes, accounts: database.Accounts, groups: database.Authorization,
                playerAuthorization: foundation.PlayerAuthorization, roleLeases: database.RoleLeases)));
        string groupOptionsPath = Path.Combine(AppContext.BaseDirectory, "data", "ControlGroupOption.json");
        var groupOptions = File.Exists(groupOptionsPath)
            ? System.Text.Json.JsonSerializer.Deserialize<GroupAssignmentOptions>(File.ReadAllText(groupOptionsPath))
                ?? throw new InvalidDataException("ControlGroupOption.json is empty.")
            : null;
        if (groupOptions is not null && (groupOptions.Groups is null || groupOptions.Groups.Any(entry => entry.Value is null)))
            throw new InvalidDataException("ControlGroupOption.json must contain a valid Groups object.");
        var accountCommand = new AccountCommand(database.Accounts, database.Authorization, database.RoleLeases, groupAssignments: groupOptions?.Groups);
        resources.Own(commandDispatcher.Register(new SetupCommand()));
        resources.Own(commandDispatcher.Register(accountCommand));
        resources.Own(commandDispatcher.Register(new AccountInfoCommand(accountCommand)));
        resources.Own(commandDispatcher.Register(new TempGroupCommand(accountCommand)));
        resources.Own(commandDispatcher.Register(new TempStatusCommand(accountCommand)));
        resources.Own(commandDispatcher.Register(
            new GroupCommand(database.Authorization)));
        resources.Own(commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Item,
            name => TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Item, name), database.Authorization)));
        resources.Own(commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Projectile,
            name => TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Projectile, name), database.Authorization)));
        resources.Own(commandDispatcher.Register(new RestrictionCommand(restrictions, RestrictedContentKind.Tile,
            name => TerrariaReferenceDataExporter.ResolveContentName(RestrictedContentKind.Tile, name), database.Authorization)));
        resources.Own(commandDispatcher.Register(
            new RegionCommand(database.Regions, playerBoundary, playerBoundary, database.Accounts, database.Authorization, world.WorldEnvironment, accountSessions)));
        resources.Own(new PersistentBanEnforcer(
            identity.BanService,
            playerBoundary,
            playerBoundary,
            playerBoundary,
            foundation.Logger,
            accounts: accountSessions,
            dispatcher: foundation.GameThreadDispatcher));
    }

    private sealed record Identity(
        IVegaDatabase Database,
        GlobalBanService BanService,
        PlayerAccountSessionService AccountSessions,
        PlayerPlaytimeService Playtime,
        TerrariaPlayerInventoryService InventoryService,
        ChatPipeline Chat);
    private sealed record Characters(
        GameplayEnforcementOptions GameplayOptions,
        PrimaryCharacterPersistenceOptions PrimaryCharacterOptions,
        bool AuthoritativeCharacters,
        SandboxSscSessionCoordinator SandboxSscSessions,
        PluginNpcEconomyRegistry NpcEconomy,
        TerrariaNpcActorService WorldNpcActors);
    private sealed record PacketPolicies(
        PlayerInventorySanityPolicy InventoryPolicy,
        PacketProcessorPipeline<PacketView> PlayerPacketPolicies);
}
