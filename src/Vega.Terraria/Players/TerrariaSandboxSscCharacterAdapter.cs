using System.Text.Json;
using Microsoft.Xna.Framework;
using Multiplicity.Packets;
using Terraria.ID;
using Terraria.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

/// <summary>
/// Captures and restores the Terraria-owned character state used by sandbox SSC modes.
/// The serialized payload is intentionally Vega-owned so the shared sandbox store remains independent
/// from legacy database schemas and from any one Terraria process.
/// </summary>
public sealed class TerrariaSandboxSscCharacterAdapter(IGameThreadDispatcher dispatcher) : ISandboxSscCharacterAdapter
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(
            "sandbox.ssc.capture",
            () => Capture(playerId),
            cancellationToken,
            new SandboxSscCharacterCaptureResult(false, ErrorCode: "sandbox.ssc.dispatcher_unavailable"),
            new SandboxSscCharacterCaptureResult(false, ErrorCode: "sandbox.ssc.cancelled"));

    public ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(
        PlayerId playerId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        CharacterState? state;
        try
        {
            state = JsonSerializer.Deserialize<CharacterState>(payload, JsonOptions);
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(new SandboxSscCharacterApplyResult(false, "sandbox.ssc.payload_invalid"));
        }

        if (state is null || state.Version != SchemaVersion || !Validate(state))
        {
            return ValueTask.FromResult(new SandboxSscCharacterApplyResult(false, "sandbox.ssc.payload_invalid"));
        }

        return DispatchAsync(
            "sandbox.ssc.apply",
            () => Apply(playerId, state),
            cancellationToken,
            new SandboxSscCharacterApplyResult(false, "sandbox.ssc.dispatcher_unavailable"),
            new SandboxSscCharacterApplyResult(false, "sandbox.ssc.cancelled"));
    }

    private SandboxSscCharacterCaptureResult Capture(PlayerId playerId)
    {
        dispatcher.AssertGameThread();
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player) || player is null)
        {
            return new SandboxSscCharacterCaptureResult(false, ErrorCode: "player.not_found");
        }

        var state = new CharacterState
        {
            Version = SchemaVersion,
            Life = Math.Max(1, player.statLife),
            MaxLife = player.statLifeMax,
            Mana = player.statMana,
            MaxMana = player.statManaMax,
            SpawnX = player.SpawnX,
            SpawnY = player.SpawnY,
            ExtraAccessory = player.extraAccessory,
            Difficulty = player.difficulty,
            SkinVariant = player.skinVariant,
            Hair = player.hair,
            HairDye = player.hairDye,
            HairColor = ColorState.From(player.hairColor),
            PantsColor = ColorState.From(player.pantsColor),
            ShirtColor = ColorState.From(player.shirtColor),
            UnderShirtColor = ColorState.From(player.underShirtColor),
            ShoeColor = ColorState.From(player.shoeColor),
            SkinColor = ColorState.From(player.skinColor),
            EyeColor = ColorState.From(player.eyeColor),
            HideVisuals = [.. player.hideVisibleAccessory],
            QuestsCompleted = player.anglerQuestsFinished,
            UsingBiomeTorches = player.UsingBiomeTorches,
            HappyFunTorchTime = player.happyFunTorchTime,
            UnlockedBiomeTorches = player.unlockedBiomeTorches,
            CurrentLoadoutIndex = player.CurrentLoadoutIndex,
            AteArtisanBread = player.ateArtisanBread,
            UsedAegisCrystal = player.usedAegisCrystal,
            UsedAegisFruit = player.usedAegisFruit,
            UsedArcaneCrystal = player.usedArcaneCrystal,
            UsedGalaxyPearl = player.usedGalaxyPearl,
            UsedGummyWorm = player.usedGummyWorm,
            UsedAmbrosia = player.usedAmbrosia,
            UnlockedSuperCart = player.unlockedSuperCart,
            EnabledSuperCart = player.enabledSuperCart,
            DeathsPve = player.numberOfDeathsPVE,
            DeathsPvp = player.numberOfDeathsPVP,
            VoiceVariant = player.voiceVariant,
            VoicePitchOffset = player.voicePitchOffset,
            Team = player.team,
            Inventory = CaptureItems(player.inventory),
            Armor = CaptureItems(player.armor),
            Dye = CaptureItems(player.dye),
            MiscEquips = CaptureItems(player.miscEquips),
            MiscDyes = CaptureItems(player.miscDyes),
            Piggy = CaptureItems(player.bank.item),
            Safe = CaptureItems(player.bank2.item),
            Forge = CaptureItems(player.bank3.item),
            VoidVault = CaptureItems(player.bank4.item),
            Trash = ItemState.From(player.trashItem),
            Loadouts = [.. player.Loadouts.Select(loadout => new LoadoutState
            {
                Armor = CaptureItems(loadout.Armor),
                Dye = CaptureItems(loadout.Dye)
            })]
        };

        return new SandboxSscCharacterCaptureResult(true, JsonSerializer.Serialize(state, JsonOptions));
    }

    private SandboxSscCharacterApplyResult Apply(PlayerId playerId, CharacterState state)
    {
        dispatcher.AssertGameThread();
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player) || player is null)
        {
            return new SandboxSscCharacterApplyResult(false, "player.not_found");
        }

        try
        {
            player.statLife = state.Life;
            player.statLifeMax = state.MaxLife;
            player.statMana = state.Mana;
            player.statManaMax = state.MaxMana;
            player.SpawnX = state.SpawnX;
            player.SpawnY = state.SpawnY;
            player.extraAccessory = state.ExtraAccessory;
            player.difficulty = state.Difficulty;
            player.skinVariant = state.SkinVariant;
            player.hair = state.Hair;
            player.hairDye = state.HairDye;
            player.hairColor = state.HairColor.ToColor();
            player.pantsColor = state.PantsColor.ToColor();
            player.shirtColor = state.ShirtColor.ToColor();
            player.underShirtColor = state.UnderShirtColor.ToColor();
            player.shoeColor = state.ShoeColor.ToColor();
            player.skinColor = state.SkinColor.ToColor();
            player.eyeColor = state.EyeColor.ToColor();
            player.hideVisibleAccessory = state.HideVisuals.Length == player.hideVisibleAccessory.Length
                ? [.. state.HideVisuals]
                : new bool[player.hideVisibleAccessory.Length];
            player.anglerQuestsFinished = state.QuestsCompleted;
            player.UsingBiomeTorches = state.UsingBiomeTorches;
            player.happyFunTorchTime = state.HappyFunTorchTime;
            player.unlockedBiomeTorches = state.UnlockedBiomeTorches;
            player.CurrentLoadoutIndex = Math.Clamp(state.CurrentLoadoutIndex, 0, Math.Max(0, player.Loadouts.Length - 1));
            player.ateArtisanBread = state.AteArtisanBread;
            player.usedAegisCrystal = state.UsedAegisCrystal;
            player.usedAegisFruit = state.UsedAegisFruit;
            player.usedArcaneCrystal = state.UsedArcaneCrystal;
            player.usedGalaxyPearl = state.UsedGalaxyPearl;
            player.usedGummyWorm = state.UsedGummyWorm;
            player.usedAmbrosia = state.UsedAmbrosia;
            player.unlockedSuperCart = state.UnlockedSuperCart;
            player.enabledSuperCart = state.EnabledSuperCart;
            player.numberOfDeathsPVE = state.DeathsPve;
            player.numberOfDeathsPVP = state.DeathsPvp;
            player.voiceVariant = state.VoiceVariant;
            player.voicePitchOffset = state.VoicePitchOffset;
            player.team = state.Team;

            ApplyItems(player.inventory, state.Inventory);
            ApplyItems(player.armor, state.Armor);
            ApplyItems(player.dye, state.Dye);
            ApplyItems(player.miscEquips, state.MiscEquips);
            ApplyItems(player.miscDyes, state.MiscDyes);
            ApplyItems(player.bank.item, state.Piggy);
            ApplyItems(player.bank2.item, state.Safe);
            ApplyItems(player.bank3.item, state.Forge);
            ApplyItems(player.bank4.item, state.VoidVault);
            ApplyItem(player.trashItem, state.Trash);
            for (int index = 0; index < player.Loadouts.Length && index < state.Loadouts.Length; index++)
            {
                ApplyItems(player.Loadouts[index].Armor, state.Loadouts[index].Armor);
                ApplyItems(player.Loadouts[index].Dye, state.Loadouts[index].Dye);
            }

            Array.Clear(player.buffType);
            Array.Clear(player.buffTime);
            SyncCharacter(player);
            return new SandboxSscCharacterApplyResult(true);
        }
        catch (Exception)
        {
            return new SandboxSscCharacterApplyResult(false, "sandbox.ssc.apply_failed");
        }
    }

    private static ItemState[] CaptureItems(global::Terraria.Item[] items) =>
        [.. items.Select(ItemState.From)];

    private static void ApplyItems(global::Terraria.Item[] target, ItemState[] source)
    {
        for (int index = 0; index < target.Length; index++)
        {
            ApplyItem(target[index], index < source.Length ? source[index] : ItemState.Empty);
        }
    }

    private static void ApplyItem(global::Terraria.Item item, ItemState state)
    {
        item.netDefaults(state.Type);
        if (state.Type == 0)
        {
            item.stack = 0;
            item.favorited = false;
            return;
        }

        item.stack = Math.Clamp(state.Stack, 1, Math.Max(1, item.maxStack));
        item.prefix = state.Prefix;
        item.favorited = state.Favorited;
    }

    private static bool Validate(CharacterState state)
    {
        if (state.Life <= 0 || state.MaxLife <= 0 || state.Life > state.MaxLife ||
            state.Mana < 0 || state.MaxMana < 0 || state.Mana > state.MaxMana ||
            state.Loadouts.Length > 3)
        {
            return false;
        }

        return AllItems(state).All(static item =>
            item.Type >= 0 && item.Type < ItemID.Count && item.Stack >= 0);
    }

    private static IEnumerable<ItemState> AllItems(CharacterState state) =>
        state.Inventory
            .Concat(state.Armor)
            .Concat(state.Dye)
            .Concat(state.MiscEquips)
            .Concat(state.MiscDyes)
            .Concat(state.Piggy)
            .Concat(state.Safe)
            .Concat(state.Forge)
            .Concat(state.VoidVault)
            .Append(state.Trash)
            .Concat(state.Loadouts.SelectMany(static loadout => loadout.Armor.Concat(loadout.Dye)));

    private static void SyncCharacter(global::Terraria.Player player)
    {
        int index = player.whoAmI;
        global::Terraria.NetMessage.SendData(
            (int)PacketTypes.SyncLoadout,
            index,
            -1,
            NetworkText.Empty,
            index,
            player.CurrentLoadoutIndex);
        global::Terraria.NetMessage.SendData(
            (int)PacketTypes.SyncLoadout,
            -1,
            index,
            NetworkText.Empty,
            index,
            player.CurrentLoadoutIndex);

        SyncSlots(index, PlayerItemSlotID.Inventory0, player.inventory.Length);
        SyncSlots(index, PlayerItemSlotID.Armor0, player.armor.Length);
        SyncSlots(index, PlayerItemSlotID.Dye0, player.dye.Length);
        SyncSlots(index, PlayerItemSlotID.Misc0, player.miscEquips.Length);
        SyncSlots(index, PlayerItemSlotID.MiscDye0, player.miscDyes.Length);
        SyncSlots(index, PlayerItemSlotID.Bank1_0, player.bank.item.Length);
        SyncSlots(index, PlayerItemSlotID.Bank2_0, player.bank2.item.Length);
        SyncSingleSlot(index, PlayerItemSlotID.TrashItem);
        SyncSlots(index, PlayerItemSlotID.Bank3_0, player.bank3.item.Length);
        SyncSlots(index, PlayerItemSlotID.Bank4_0, player.bank4.item.Length);
        if (player.Loadouts.Length >= 3)
        {
            SyncSlots(index, PlayerItemSlotID.Loadout1_Armor_0, player.Loadouts[0].Armor.Length);
            SyncSlots(index, PlayerItemSlotID.Loadout1_Dye_0, player.Loadouts[0].Dye.Length);
            SyncSlots(index, PlayerItemSlotID.Loadout2_Armor_0, player.Loadouts[1].Armor.Length);
            SyncSlots(index, PlayerItemSlotID.Loadout2_Dye_0, player.Loadouts[1].Dye.Length);
            SyncSlots(index, PlayerItemSlotID.Loadout3_Armor_0, player.Loadouts[2].Armor.Length);
            SyncSlots(index, PlayerItemSlotID.Loadout3_Dye_0, player.Loadouts[2].Dye.Length);
        }

        global::Terraria.NetMessage.SendData(4, -1, -1, NetworkText.FromLiteral(player.name), index);
        global::Terraria.NetMessage.SendData(42, -1, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(16, -1, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(50, -1, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(50, index, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(76, index, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(76, -1, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(45, index, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(45, -1, -1, NetworkText.Empty, index);
    }

    private static void SyncSlots(int playerIndex, int firstSlot, int count)
    {
        for (int offset = 0; offset < count; offset++)
        {
            SyncSingleSlot(playerIndex, firstSlot + offset);
        }
    }

    private static void SyncSingleSlot(int playerIndex, int slot)
    {
        global::Terraria.NetMessage.SendData(5, -1, -1, NetworkText.Empty, playerIndex, slot);
        global::Terraria.NetMessage.SendData(5, playerIndex, -1, NetworkText.Empty, playerIndex, slot);
    }

    private static bool TryGetPlayer(PlayerId playerId, out global::Terraria.Player? player)
    {
        if ((uint)playerId.Slot < (uint)global::Terraria.Main.player.Length &&
            global::Terraria.Main.player[playerId.Slot] is { } candidate)
        {
            player = candidate;
            return true;
        }

        player = null;
        return false;
    }

    private ValueTask<T> DispatchAsync<T>(
        string operation,
        Func<T> action,
        CancellationToken cancellationToken,
        T unavailable,
        T cancelled)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(cancelled);
        }

        if (!dispatcher.IsGameThreadKnown)
        {
            return ValueTask.FromResult(unavailable);
        }

        if (dispatcher.IsGameThread)
        {
            return ValueTask.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryPost(operation, () => completion.TrySetResult(
                cancellationToken.IsCancellationRequested ? cancelled : action())))
        {
            return ValueTask.FromResult(unavailable);
        }

        return new ValueTask<T>(completion.Task);
    }

    public sealed class CharacterState
    {
        public int Version { get; init; }
        public int Life { get; init; }
        public int MaxLife { get; init; }
        public int Mana { get; init; }
        public int MaxMana { get; init; }
        public int SpawnX { get; init; }
        public int SpawnY { get; init; }
        public bool ExtraAccessory { get; init; }
        public byte Difficulty { get; init; }
        public int SkinVariant { get; init; }
        public int Hair { get; init; }
        public byte HairDye { get; init; }
        public ColorState HairColor { get; init; }
        public ColorState PantsColor { get; init; }
        public ColorState ShirtColor { get; init; }
        public ColorState UnderShirtColor { get; init; }
        public ColorState ShoeColor { get; init; }
        public ColorState SkinColor { get; init; }
        public ColorState EyeColor { get; init; }
        public bool[] HideVisuals { get; init; } = [];
        public int QuestsCompleted { get; init; }
        public bool UsingBiomeTorches { get; init; }
        public bool HappyFunTorchTime { get; init; }
        public bool UnlockedBiomeTorches { get; init; }
        public int CurrentLoadoutIndex { get; init; }
        public bool AteArtisanBread { get; init; }
        public bool UsedAegisCrystal { get; init; }
        public bool UsedAegisFruit { get; init; }
        public bool UsedArcaneCrystal { get; init; }
        public bool UsedGalaxyPearl { get; init; }
        public bool UsedGummyWorm { get; init; }
        public bool UsedAmbrosia { get; init; }
        public bool UnlockedSuperCart { get; init; }
        public bool EnabledSuperCart { get; init; }
        public int DeathsPve { get; init; }
        public int DeathsPvp { get; init; }
        public int VoiceVariant { get; init; }
        public float VoicePitchOffset { get; init; }
        public int Team { get; init; }
        public ItemState[] Inventory { get; init; } = [];
        public ItemState[] Armor { get; init; } = [];
        public ItemState[] Dye { get; init; } = [];
        public ItemState[] MiscEquips { get; init; } = [];
        public ItemState[] MiscDyes { get; init; } = [];
        public ItemState[] Piggy { get; init; } = [];
        public ItemState[] Safe { get; init; } = [];
        public ItemState[] Forge { get; init; } = [];
        public ItemState[] VoidVault { get; init; } = [];
        public ItemState Trash { get; init; } = ItemState.Empty;
        public LoadoutState[] Loadouts { get; init; } = [];
    }

    public readonly record struct ItemState(int Type, int Stack, byte Prefix, bool Favorited)
    {
        public static ItemState Empty { get; } = new(0, 0, 0, false);
        public static ItemState From(global::Terraria.Item item) =>
            new(item.type, item.stack, item.prefix, item.favorited);
    }

    public sealed class LoadoutState
    {
        public ItemState[] Armor { get; init; } = [];
        public ItemState[] Dye { get; init; } = [];
    }

    public readonly record struct ColorState(byte R, byte G, byte B, byte A)
    {
        public static ColorState From(Color color) => new(color.R, color.G, color.B, color.A);
        public Color ToColor() => new(R, G, B, A);
    }
}
