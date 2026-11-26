using System.Text.Json;
using Microsoft.Xna.Framework;
using Multiplicity.Packets;
using Terraria.ID;
using Terraria.Localization;
using Vega.Contracts.Players;
using Vega.Contracts.Accounts;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Threading;

namespace Vega.Terraria.Players;

/// <summary>
/// Captures and restores the Terraria-owned character state used by sandbox SSC modes.
/// The serialized payload is intentionally Vega-owned so the shared sandbox store remains independent
/// from legacy database schemas and from any one Terraria process.
/// </summary>
public sealed class TerrariaSandboxSscCharacterAdapter(IGameThreadDispatcher dispatcher, TerrariaPlayerBoundary? players = null) : ISandboxSscCharacterAdapter
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object earlyJoinSync = new();
    private readonly Dictionary<int, EarlyJoinCapture> earlyJoin = [];

    /// <summary>Native pre-WorldData hook: receives initial character data before the player is active.</summary>
    public void ObserveCharacterReceived(int slot, global::Terraria.Net.Sockets.ISocket socket)
    {
        if (!dispatcher.IsGameThread || (uint)slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[slot] is not { State: 2 } client || !ReferenceEquals(client.Socket, socket)) return;
        SandboxSscCharacterCaptureResult captured;
        try { captured = EncodeCapture(CaptureSnapshot(new PlayerId(slot))); }
        catch (Exception) { captured = new(false, ErrorCode: "ssc.join_snapshot_unavailable"); }
        if (captured.Payload is { Length: > 131072 }) captured = new(false, ErrorCode: "ssc.join_snapshot_too_large");
        lock (earlyJoinSync)
        {
            if (!earlyJoin.TryGetValue(slot, out var previous) || !ReferenceEquals(previous.Socket, socket))
                earlyJoin[slot] = new(socket, captured);
        }
    }

    public void ForgetCharacterReceived(int slot, global::Terraria.Net.Sockets.ISocket socket)
    {
        lock (earlyJoinSync)
            if (earlyJoin.TryGetValue(slot, out var capture) && ReferenceEquals(capture.Socket, socket)) earlyJoin.Remove(slot);
    }

    // Once the initial client character has been captured for explicit administrator import,
    // the SSC world starts this exact connection with an empty server-owned pending character.
    public bool PrepareAuthoritativeJoin(int slot, global::Terraria.Net.Sockets.ISocket socket)
    {
        if (!dispatcher.IsGameThread || (uint)slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[slot] is not { State: 2 } client || !ReferenceEquals(client.Socket, socket)) return false;
        return Apply(new PlayerId(slot), CreateStarterState(global::Terraria.Main.GameMode == 3, includeItems: false), synchronize: false).Succeeded;
    }

    public bool SynchronizePendingJoin(int slot, global::Terraria.Net.Sockets.ISocket socket)
    {
        if (!dispatcher.IsGameThread || (uint)slot >= (uint)global::Terraria.Netplay.Clients.Length ||
            global::Terraria.Netplay.Clients[slot] is not { State: 10 } client || !ReferenceEquals(client.Socket, socket) ||
            !TryGetPlayer(new PlayerId(slot), out var player) || player is null) return false;
        SyncCharacter(player, broadcast: false);
        return true;
    }

    public bool ResetPendingCharacter(PlayerSnapshot expected) => dispatcher.IsGameThread &&
        players?.TryGetSessionPlayer(expected, out _) == true &&
        Apply(expected.Id, CreateStarterState(global::Terraria.Main.GameMode == 3, includeItems: false)).Succeeded;

    public ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(
        PlayerId playerId,
        CancellationToken cancellationToken = default) =>
        CaptureDetachedAsync(
            "sandbox.ssc.capture",
            () => CaptureSnapshot(playerId),
            cancellationToken);

    public ValueTask<SandboxSscCharacterCaptureResult> CaptureAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        CaptureDetachedAsync("sandbox.ssc.capture", () => players?.TryGetSessionPlayer(player, out _) == true
            ? CaptureSnapshot(player.Id) : new CaptureSnapshotResult(null, "sandbox.ssc.session_changed"), cancellationToken);

    public ValueTask<SandboxSscCharacterCaptureResult> CaptureJoinAsync(PlayerSnapshot player, CancellationToken cancellationToken = default) =>
        DispatchAsync("ssc.join.capture", () =>
        {
            if (players?.TryGetSessionPlayer(player, out _) != true)
                return new SandboxSscCharacterCaptureResult(false, ErrorCode: "sandbox.ssc.session_changed");
            int slot = player.Id.Slot;
            var socket = global::Terraria.Netplay.Clients[slot]?.Socket;
            lock (earlyJoinSync)
            {
                if (earlyJoin.Remove(slot, out var capture) && ReferenceEquals(capture.Socket, socket))
                    return capture.Result;
            }
            // A missing early snapshot is not interchangeable with a later character state.
            return new SandboxSscCharacterCaptureResult(false, ErrorCode: "ssc.join_snapshot_unavailable");
        }, cancellationToken, new SandboxSscCharacterCaptureResult(false, ErrorCode: "sandbox.ssc.dispatcher_unavailable"),
            new SandboxSscCharacterCaptureResult(false, ErrorCode: "sandbox.ssc.cancelled"));

    private sealed record EarlyJoinCapture(global::Terraria.Net.Sockets.ISocket Socket, SandboxSscCharacterCaptureResult Result);

    public ValueTask<SandboxSscCharacterCaptureResult> CreateInitialAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        PlayerAccountBinding binding, CancellationToken cancellationToken = default) =>
        CaptureDetachedAsync("ssc.primary.initial", () => WithBinding(player, accounts, binding,
            () => new CaptureSnapshotResult(CreateStarterState(global::Terraria.Main.GameMode == 3, includeItems: true)),
            new CaptureSnapshotResult(null, "ssc.binding_changed")), cancellationToken);

    public ValueTask<SandboxSscCharacterCaptureResult> CaptureAuthenticatedAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        PlayerAccountBinding binding, CancellationToken cancellationToken = default) =>
        CaptureDetachedAsync("ssc.primary.capture", () => WithBinding(player, accounts, binding, () => CaptureSnapshot(player.Id),
            new CaptureSnapshotResult(null, "ssc.binding_changed")), cancellationToken);

    public ValueTask<SandboxSscCharacterApplyResult> ApplyAuthenticatedAsync(PlayerSnapshot player, IPlayerAccountSessionService accounts,
        PlayerAccountBinding binding, string payload, CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(player.Id, player, payload, cancellationToken, accounts, binding);

    private T WithBinding<T>(PlayerSnapshot player, IPlayerAccountSessionService accounts, PlayerAccountBinding binding, Func<T> action, T rejected)
    {
        if (binding.PlayerId != player.Id || binding.SessionId != player.SessionId || players?.TryGetSessionPlayer(player, out _) != true)
            return rejected;
        return accounts.TryRunWithBinding(binding, action, out T result) ? result : rejected;
    }

    public ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(
        PlayerId playerId,
        string payload,
        CancellationToken cancellationToken = default) => ApplyCoreAsync(playerId, null, payload, cancellationToken);

    public ValueTask<SandboxSscCharacterApplyResult> ApplyAsync(PlayerSnapshot player, string payload, CancellationToken cancellationToken = default) =>
        ApplyCoreAsync(player.Id, player, payload, cancellationToken);

    private ValueTask<SandboxSscCharacterApplyResult> ApplyCoreAsync(PlayerId playerId, PlayerSnapshot? expected, string payload, CancellationToken cancellationToken,
        IPlayerAccountSessionService? accounts = null, PlayerAccountBinding? binding = null)
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
            () => expected is not null && accounts is not null && binding is { } accountBinding
                ? WithBinding(expected, accounts, accountBinding, () => Apply(playerId, state), new SandboxSscCharacterApplyResult(false, "ssc.binding_changed"))
                : expected is null || players?.TryGetSessionPlayer(expected, out _) == true
                ? Apply(playerId, state) : new SandboxSscCharacterApplyResult(false, "sandbox.ssc.session_changed"),
            cancellationToken,
            new SandboxSscCharacterApplyResult(false, "sandbox.ssc.dispatcher_unavailable"),
            new SandboxSscCharacterApplyResult(false, "sandbox.ssc.cancelled"));
    }

    // The existing SSC reconciliation worker awaits this operation one character at a time.
    // Dispatch only the deep native-state copy; RunContinuationsAsynchronously and
    // ConfigureAwait(false) keep JSON encoding off the Terraria thread for worker callers.
    // A game-thread caller still completes synchronously: pre-reset disconnect snapshots
    // must be captured and encoded before the native player slot/account binding is retired.
    internal ValueTask<SandboxSscCharacterCaptureResult> CaptureDetachedAsync(
        string operation, Func<CaptureSnapshotResult> capture, CancellationToken cancellationToken)
    {
        ValueTask<CaptureSnapshotResult> pending = DispatchAsync(operation, capture, cancellationToken,
            new CaptureSnapshotResult(null, "sandbox.ssc.dispatcher_unavailable"),
            new CaptureSnapshotResult(null, "sandbox.ssc.cancelled"));
        return pending.IsCompletedSuccessfully
            ? ValueTask.FromResult(EncodeCapture(pending.Result))
            : EncodeWhenReadyAsync(pending);

        static async ValueTask<SandboxSscCharacterCaptureResult> EncodeWhenReadyAsync(ValueTask<CaptureSnapshotResult> pending) =>
            EncodeCapture(await pending.ConfigureAwait(false));
    }

    internal readonly record struct CaptureSnapshotResult(CharacterState? State, string? ErrorCode = null);

    private static SandboxSscCharacterCaptureResult EncodeCapture(CaptureSnapshotResult capture) => capture.State is not { } state
        ? new SandboxSscCharacterCaptureResult(false, ErrorCode: capture.ErrorCode)
        : Validate(state) ? new SandboxSscCharacterCaptureResult(true, JsonSerializer.Serialize(state, JsonOptions))
            : new SandboxSscCharacterCaptureResult(false, ErrorCode: "sandbox.ssc.capture_invalid");

    private CaptureSnapshotResult CaptureSnapshot(PlayerId playerId)
    {
        dispatcher.AssertGameThread();
        if (!TryGetPlayer(playerId, out global::Terraria.Player? player) || player is null)
        {
            return new CaptureSnapshotResult(null, "player.not_found");
        }

        var state = new CharacterState
        {
            Version = SchemaVersion,
            Life = Math.Clamp(player.statLife, 1, Math.Max(1, player.statLifeMax)),
            MaxLife = player.statLifeMax,
            Mana = Math.Clamp(player.statMana, 0, Math.Max(0, player.statManaMax)),
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

        return new CaptureSnapshotResult(state);
    }

    private SandboxSscCharacterApplyResult Apply(PlayerId playerId, CharacterState state, bool synchronize = true)
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
            for (int index = 0; index < player.Loadouts.Length; index++)
            {
                ApplyItems(player.Loadouts[index].Armor, index < state.Loadouts.Length ? state.Loadouts[index].Armor : []);
                ApplyItems(player.Loadouts[index].Dye, index < state.Loadouts.Length ? state.Loadouts[index].Dye : []);
            }

            Array.Clear(player.buffType);
            Array.Clear(player.buffTime);
            if (synchronize) SyncCharacter(player);
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
            state.Loadouts is null || state.Loadouts.Length > 3 || state.Loadouts.Any(loadout => loadout is null || loadout.Armor is null || loadout.Dye is null) ||
            state.HideVisuals is null || state.Inventory is null || state.Armor is null || state.Dye is null || state.MiscEquips is null ||
            state.MiscDyes is null || state.Piggy is null || state.Safe is null || state.Forge is null || state.VoidVault is null)
        {
            return false;
        }

        return AllItems(state).All(static item =>
            item.Type >= 0 && item.Type < ItemID.Count && item.Stack >= 0);
    }

    internal static string CreateStarterPayload(bool journey) => JsonSerializer.Serialize(CreateStarterState(journey, includeItems: true), JsonOptions);

    /// <summary>Encode an explicitly validated offline migration state using the current native adapter schema.</summary>
    public static string SerializeImportedCharacter(CharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != SchemaVersion || !Validate(state))
            throw new InvalidDataException("Imported character does not match the native SSC schema.");
        return JsonSerializer.Serialize(state, JsonOptions);
    }

    internal static CharacterState CreateStarterState(bool journey, bool includeItems) => new()
    {
        Version = SchemaVersion, Life = 100, MaxLife = 100, Mana = 20, MaxMana = 20,
        SpawnX = -1, SpawnY = -1, Difficulty = journey ? (byte)3 : (byte)0,
        HairColor = new(92, 68, 48, 255), SkinColor = new(255, 205, 148, 255), EyeColor = new(105, 90, 75, 255),
        ShirtColor = new(80, 130, 180, 255), UnderShirtColor = new(160, 160, 160, 255),
        PantsColor = new(80, 90, 110, 255), ShoeColor = new(80, 60, 45, 255),
        Inventory = includeItems ? [new(ItemID.CopperShortsword, 1, 0, false), new(ItemID.CopperPickaxe, 1, 0, false), new(ItemID.CopperAxe, 1, 0, false)] : []
    };

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

    private static void SyncCharacter(global::Terraria.Player player, bool broadcast = true)
    {
        int index = player.whoAmI;
        global::Terraria.NetMessage.SendData(
            (int)PacketTypes.SyncLoadout,
            index,
            -1,
            NetworkText.Empty,
            index,
            player.CurrentLoadoutIndex);
        if (broadcast) global::Terraria.NetMessage.SendData(
            (int)PacketTypes.SyncLoadout,
            -1,
            index,
            NetworkText.Empty,
            index,
            player.CurrentLoadoutIndex);

        SyncSlots(index, PlayerItemSlotID.Inventory0, player.inventory.Length, broadcast);
        SyncSlots(index, PlayerItemSlotID.Armor0, player.armor.Length, broadcast);
        SyncSlots(index, PlayerItemSlotID.Dye0, player.dye.Length, broadcast);
        SyncSlots(index, PlayerItemSlotID.Misc0, player.miscEquips.Length, broadcast);
        SyncSlots(index, PlayerItemSlotID.MiscDye0, player.miscDyes.Length, broadcast);
        SyncSlots(index, PlayerItemSlotID.Bank1_0, player.bank.item.Length, broadcast: false);
        SyncSlots(index, PlayerItemSlotID.Bank2_0, player.bank2.item.Length, broadcast: false);
        SyncSingleSlot(index, PlayerItemSlotID.TrashItem, broadcast: false);
        SyncSlots(index, PlayerItemSlotID.Bank3_0, player.bank3.item.Length, broadcast: false);
        SyncSlots(index, PlayerItemSlotID.Bank4_0, player.bank4.item.Length, broadcast: false);
        if (player.Loadouts.Length >= 3)
        {
            SyncSlots(index, PlayerItemSlotID.Loadout1_Armor_0, player.Loadouts[0].Armor.Length, broadcast);
            SyncSlots(index, PlayerItemSlotID.Loadout1_Dye_0, player.Loadouts[0].Dye.Length, broadcast);
            SyncSlots(index, PlayerItemSlotID.Loadout2_Armor_0, player.Loadouts[1].Armor.Length, broadcast);
            SyncSlots(index, PlayerItemSlotID.Loadout2_Dye_0, player.Loadouts[1].Dye.Length, broadcast);
            SyncSlots(index, PlayerItemSlotID.Loadout3_Armor_0, player.Loadouts[2].Armor.Length, broadcast);
            SyncSlots(index, PlayerItemSlotID.Loadout3_Dye_0, player.Loadouts[2].Dye.Length, broadcast);
        }

        int destination = broadcast ? -1 : index;
        global::Terraria.NetMessage.SendData(4, destination, -1, NetworkText.FromLiteral(player.name), index);
        global::Terraria.NetMessage.SendData(42, destination, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(16, destination, -1, NetworkText.Empty, index);
        if (broadcast) global::Terraria.NetMessage.SendData(50, -1, index, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(50, index, -1, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(76, index, -1, NetworkText.Empty, index);
        if (broadcast) global::Terraria.NetMessage.SendData(76, -1, index, NetworkText.Empty, index);
        global::Terraria.NetMessage.SendData(45, index, -1, NetworkText.Empty, index);
        if (broadcast) global::Terraria.NetMessage.SendData(45, -1, index, NetworkText.Empty, index);
    }

    private static void SyncSlots(int playerIndex, int firstSlot, int count, bool broadcast = true)
    {
        for (int offset = 0; offset < count; offset++)
        {
            SyncSingleSlot(playerIndex, firstSlot + offset, broadcast);
        }
    }

    private static void SyncSingleSlot(int playerIndex, int slot, bool broadcast = true)
    {
        if (broadcast) global::Terraria.NetMessage.SendData(5, -1, playerIndex, NetworkText.Empty, playerIndex, slot);
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
        if (!dispatcher.TryPost(operation, () =>
            {
                try { completion.TrySetResult(cancellationToken.IsCancellationRequested ? cancelled : action()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }))
        {
            return ValueTask.FromResult(unavailable);
        }

        return new ValueTask<T>(completion.Task.WaitAsync(cancellationToken));
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
