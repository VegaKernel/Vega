using Multiplicity.Packets;

namespace Vega.Terraria.Networking;

internal enum PacketPreparationClass
{
    GameThreadOnly,
    PartiallyOffloadable,
    PureBeforeApply
}

internal enum PacketFamily
{
    Other,
    PlayerState,
    Item,
    Projectile,
    Npc,
    WorldInteraction,
    WorldEvent,
    Chest,
    TileEntity,
    Social
}

/// <summary>
/// Declares how an inbound packet is treated at the Vega trust boundary. Every protocol packet must
/// land in one explicit bucket; Unknown exists only to make protocol upgrades fail tests instead of
/// silently falling through to vanilla processing.
/// </summary>
internal enum PacketIngressDisposition
{
    Unknown,
    Metadata,
    Handshake,
    EarlyValidated,
    GameplayValidated,
    ServerOnly,
    AllowedByDesign
}

internal readonly record struct PacketPipelineProfile(
    PacketFamily Family,
    PacketPreparationClass Preparation,
    PacketIngressDisposition Ingress,
    bool SecurityProcessed,
    bool OrderSensitive,
    bool WorldStateSensitive,
    bool LatencySensitive,
    bool SafelyCoalescable);

internal static class PacketPipelineClassification
{
    public const byte HighestAuditedPacketId = (byte)PacketTypes.PlayerPlatformInfo;

    public static PacketPipelineProfile Get(byte packetId) => Get((PacketTypes)packetId);

    public static PacketPipelineProfile Get(PacketTypes packetType)
    {
        PacketIngressDisposition ingress = GetIngressDisposition(packetType);
        PacketFamily family = GetFamily(packetType);
        bool processed = ingress is PacketIngressDisposition.EarlyValidated or
            PacketIngressDisposition.GameplayValidated or
            PacketIngressDisposition.ServerOnly;
        bool latencySensitive = family is PacketFamily.PlayerState or PacketFamily.Projectile;

        return new PacketPipelineProfile(
            family,
            processed ? PacketPreparationClass.PartiallyOffloadable : PacketPreparationClass.GameThreadOnly,
            ingress,
            processed,
            true,
            family is not PacketFamily.Social,
            latencySensitive,
            false);
    }

    public static PacketIngressDisposition GetIngressDisposition(PacketTypes packetType) => packetType switch
    {
        // Connection metadata is validated by TerrariaConnectionMetadataRegistry before normal packet policy.
        PacketTypes.ConnectRequest or
        PacketTypes.Placeholder or
        PacketTypes.ClientUUID or
        PacketTypes.PlayerPlatformInfo => PacketIngressDisposition.Metadata,

        // Join-state packets have their own phase/structural validation before FullyConnected.
        PacketTypes.ContinueConnecting2 or
        PacketTypes.PasswordSend or
        PacketTypes.FinishedConnectingToServer or
        PacketTypes.ClientSyncedInventory or
        PacketTypes.HostToken or
        PacketTypes.ExtraSpawnSectionLoaded or
        PacketTypes.RequestSection => PacketIngressDisposition.Handshake,

        // These are legitimate both during join and after it, so they receive an early validator and
        // the full gameplay pipeline once the connection reaches FullyConnected.
        PacketTypes.PlayerInfo or
        PacketTypes.PlayerSlot or
        PacketTypes.TileGetSection or
        PacketTypes.PlayerSpawn => PacketIngressDisposition.EarlyValidated,

        // Server-originated protocol. A fully connected client sending one of these is a protocol violation.
        PacketTypes.Disconnect or
        PacketTypes.ContinueConnecting or
        PacketTypes.WorldInfo or
        PacketTypes.Status or
        PacketTypes.TileSendSection or
        PacketTypes.TileFrameSection or
        PacketTypes.PlayerActive or
        PacketTypes.Null or
        PacketTypes.TimeSet or
        PacketTypes.ItemOwner or
        PacketTypes.NpcUpdate or
        PacketTypes.NpcItemStrike or
        PacketTypes.ChatMessage or
        PacketTypes.PlayerDamage or
        PacketTypes.EffectHeal or
        PacketTypes.PasswordRequired or
        PacketTypes.RemoveItemOwner or
        PacketTypes.EffectMana or
        PacketTypes.PlayerDeath or
        PacketTypes.PlayerSpawnSelf or
        PacketTypes.NpcUpdateBuff or
        PacketTypes.UpdateNPCName or
        PacketTypes.UpdateGoodEvil or
        PacketTypes.TravellingMerchantInventory or
        PacketTypes.AnglerQuest or
        PacketTypes.NumberOfAnglerQuestsCompleted or
        PacketTypes.CreateTemporaryAnimation or
        PacketTypes.ReportInvasionProgress or
        PacketTypes.CreateCombatText or
        PacketTypes.NpcKillCount or
        PacketTypes.UpdateTileEntity or
        PacketTypes.TweakItem or
        PacketTypes.DebugCommands or
        PacketTypes.NpcTeleportPortal or
        PacketTypes.NotifyPlayerNpcKilled or
        PacketTypes.NotifyPlayerOfEvent or
        PacketTypes.UpdateShieldStrengths or
        PacketTypes.NebulaLevelUp or
        PacketTypes.MoonLordCountdown or
        PacketTypes.NpcShopItem or
        PacketTypes.PoofOfSmoke or
        PacketTypes.SmartTextMessage or
        PacketTypes.TreeGrowFX or
        PacketTypes.CrystalInvasionWipeAll or
        PacketTypes.CrystalInvasionSendWaitTime or
        PacketTypes.CreateCombatTextExtended or
        PacketTypes.SyncRevengeMarker or
        PacketTypes.RemoveRevengeMarker or
        PacketTypes.PlayLegacySound or
        PacketTypes.DeadPlayer or
        PacketTypes.SyncCavernMonsterType or
        PacketTypes.SetCountsAsHostForGameplay or
        PacketTypes.SetMiscEventValues or
        PacketTypes.SyncItemsWithShimmerDeprecated or
        PacketTypes.SyncItemCannotBeTakenByEnemiesDeprecated or
        PacketTypes.SyncItemDespawn or
        PacketTypes.SyncChestSize or
        PacketTypes.ServerInfo => PacketIngressDisposition.ServerOnly,

        // Client-originating gameplay/state requests that require the semantic policy pipeline.
        PacketTypes.PlayerUpdate or
        PacketTypes.PlayerHp or
        PacketTypes.Tile or
        PacketTypes.DoorUse or
        PacketTypes.TileSendSquare or
        PacketTypes.ItemDrop or
        PacketTypes.ProjectileNew or
        PacketTypes.NpcStrike or
        PacketTypes.ProjectileDestroy or
        PacketTypes.TogglePvp or
        PacketTypes.ChestGetContents or
        PacketTypes.ChestItem or
        PacketTypes.ChestOpen or
        PacketTypes.PlaceChest or
        PacketTypes.Zones or
        PacketTypes.NpcTalk or
        PacketTypes.PlayerAnimation or
        PacketTypes.PlayerMana or
        PacketTypes.PlayerTeam or
        PacketTypes.SignRead or
        PacketTypes.SignNew or
        PacketTypes.LiquidSet or
        PacketTypes.PlayerBuff or
        PacketTypes.NpcSpecial or
        PacketTypes.ChestUnlock or
        PacketTypes.NpcAddBuff or
        PacketTypes.PlayerAddBuff or
        PacketTypes.PlayHarp or
        PacketTypes.HitSwitch or
        PacketTypes.UpdateNPCHome or
        PacketTypes.SpawnBossorInvasion or
        PacketTypes.PlayerDodge or
        PacketTypes.PaintTile or
        PacketTypes.PaintWall or
        PacketTypes.Teleport or
        PacketTypes.PlayerHealOther or
        PacketTypes.ChestName or
        PacketTypes.CatchNPC or
        PacketTypes.ReleaseNPC or
        PacketTypes.TeleportationPotion or
        PacketTypes.CompleteAnglerQuest or
        PacketTypes.PlaceObject or
        PacketTypes.SyncPlayerChestIndex or
        PacketTypes.LoadNetModule or
        PacketTypes.PlayerStealth or
        PacketTypes.ForceItemIntoNearestChest or
        PacketTypes.PlaceTileEntity or
        PacketTypes.PlaceItemFrame or
        PacketTypes.UpdateItemDrop or
        PacketTypes.EmoteBubble or
        PacketTypes.SyncExtraValue or
        PacketTypes.SocialHandshake or
        PacketTypes.KillPortal or
        PacketTypes.PlayerTeleportPortal or
        PacketTypes.UpdateMinionTarget or
        PacketTypes.GemLockToggle or
        PacketTypes.WiredCannonShot or
        PacketTypes.MassWireOperation or
        PacketTypes.MassWireOperationPay or
        PacketTypes.ToggleParty or
        PacketTypes.CrystalInvasionStart or
        PacketTypes.MinionAttackTargetUpdate or
        PacketTypes.PlayerHurtV2 or
        PacketTypes.PlayerDeathV2 or
        PacketTypes.Emoji or
        PacketTypes.TileEntityDisplayDollItemSync or
        PacketTypes.RequestTileEntityInteraction or
        PacketTypes.WeaponsRackTryPlacing or
        PacketTypes.TileEntityHatRackItemSync or
        PacketTypes.SyncTilePicking or
        PacketTypes.LandGolfBallInCup or
        PacketTypes.FishOutNPC or
        PacketTypes.TamperWithNPC or
        PacketTypes.FoodPlatterTryPlacing or
        PacketTypes.UpdatePlayerLuckFactors or
        PacketTypes.RequestNPCBuffRemoval or
        PacketTypes.RequestLucyPopup or
        PacketTypes.SyncProjectileTrackers or
        PacketTypes.CrystalInvasionRequestedToSkipWaitTime or
        PacketTypes.RequestQuestEffect or
        PacketTypes.ShimmerActions or
        PacketTypes.SyncLoadout or
        PacketTypes.TEDeadCellsDisplayJar or
        PacketTypes.SpectatePlayer or
        PacketTypes.ItemUseSound or
        PacketTypes.NpcHurtByDebuff or
        PacketTypes.TELeashedEntityAnchorPlaceItem or
        PacketTypes.TeamChangeFromUI or
        PacketTypes.SyncItemPosition or
        PacketTypes.DamageNPCAck => PacketIngressDisposition.GameplayValidated,

        // Ping is intentionally bidirectional and carries no mutable game authority.
        PacketTypes.Ping => PacketIngressDisposition.AllowedByDesign,

        _ => PacketIngressDisposition.Unknown
    };

    private static PacketFamily GetFamily(PacketTypes packetType) => packetType switch
    {
        PacketTypes.PlayerInfo or
        PacketTypes.PlayerSlot or
        PacketTypes.PlayerSpawn or
        PacketTypes.PlayerUpdate or
        PacketTypes.PlayerHp or
        PacketTypes.Zones or
        PacketTypes.PlayerAnimation or
        PacketTypes.PlayerMana or
        PacketTypes.PlayerTeam or
        PacketTypes.TeamChangeFromUI or
        PacketTypes.TogglePvp or
        PacketTypes.PlayerBuff or
        PacketTypes.PlayerAddBuff or
        PacketTypes.PlayerHealOther or
        PacketTypes.PlayerStealth or
        PacketTypes.PlayerHurtV2 or
        PacketTypes.PlayerDeathV2 or
        PacketTypes.SyncLoadout or
        PacketTypes.SpectatePlayer or
        PacketTypes.PlayerTeleportPortal => PacketFamily.PlayerState,

        PacketTypes.ItemDrop or
        PacketTypes.UpdateItemDrop or
        PacketTypes.SyncItemPosition or
        PacketTypes.ForceItemIntoNearestChest or
        PacketTypes.FoodPlatterTryPlacing => PacketFamily.Item,

        PacketTypes.ProjectileNew or
        PacketTypes.ProjectileDestroy or
        PacketTypes.SyncProjectileTrackers => PacketFamily.Projectile,

        PacketTypes.NpcStrike or
        PacketTypes.NpcTalk or
        PacketTypes.NpcSpecial or
        PacketTypes.NpcAddBuff or
        PacketTypes.UpdateNPCHome or
        PacketTypes.CatchNPC or
        PacketTypes.ReleaseNPC or
        PacketTypes.FishOutNPC or
        PacketTypes.TamperWithNPC or
        PacketTypes.NpcHurtByDebuff or
        PacketTypes.RequestNPCBuffRemoval => PacketFamily.Npc,

        PacketTypes.Tile or
        PacketTypes.TileSendSquare or
        PacketTypes.DoorUse or
        PacketTypes.PaintTile or
        PacketTypes.PaintWall or
        PacketTypes.SignRead or
        PacketTypes.SignNew or
        PacketTypes.PlaceObject or
        PacketTypes.PlaceItemFrame or
        PacketTypes.LiquidSet or
        PacketTypes.GemLockToggle or
        PacketTypes.WiredCannonShot or
        PacketTypes.MassWireOperation or
        PacketTypes.MassWireOperationPay or
        PacketTypes.HitSwitch => PacketFamily.WorldInteraction,

        PacketTypes.SpawnBossorInvasion or
        PacketTypes.ToggleParty or
        PacketTypes.CrystalInvasionStart or
        PacketTypes.CrystalInvasionRequestedToSkipWaitTime or
        PacketTypes.CompleteAnglerQuest or
        PacketTypes.RequestQuestEffect => PacketFamily.WorldEvent,

        PacketTypes.ChestGetContents or
        PacketTypes.ChestItem or
        PacketTypes.ChestOpen or
        PacketTypes.PlaceChest or
        PacketTypes.ChestName or
        PacketTypes.ChestUnlock or
        PacketTypes.SyncPlayerChestIndex => PacketFamily.Chest,

        PacketTypes.PlaceTileEntity or
        PacketTypes.TEDeadCellsDisplayJar or
        PacketTypes.TileEntityDisplayDollItemSync or
        PacketTypes.RequestTileEntityInteraction or
        PacketTypes.WeaponsRackTryPlacing or
        PacketTypes.TileEntityHatRackItemSync or
        PacketTypes.SyncTilePicking or
        PacketTypes.TELeashedEntityAnchorPlaceItem => PacketFamily.TileEntity,

        PacketTypes.Emoji or
        PacketTypes.EmoteBubble or
        PacketTypes.SocialHandshake or
        PacketTypes.LoadNetModule => PacketFamily.Social,

        _ => PacketFamily.Other
    };
}
