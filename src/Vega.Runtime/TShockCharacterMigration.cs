using System.Globalization;
using Microsoft.Data.Sqlite;
using Vega.Contracts.Accounts;
using Vega.Terraria.Networking;
using static Vega.Terraria.Players.TerrariaSandboxSscCharacterAdapter;

namespace Vega.Runtime;

/// <summary>Import the current TShock 350-slot SSC layout without guessing older slot layouts.</summary>
internal static class TShockCharacterMigration
{
    private const int SlotCount = 350;

    public static IReadOnlyDictionary<string, string> Read(string source, IReadOnlyList<AccountSnapshot> destinationAccounts, byte difficulty)
    {
        if (difficulty is not (0 or 3)) throw new ArgumentOutOfRangeException(nameof(difficulty));
        var accounts = destinationAccounts.ToDictionary(account => account.Id.Value);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(source), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT c.*, u.Username AS SourceUsername FROM tsCharacter c LEFT JOIN Users u ON u.ID = c.Account ORDER BY c.Account;";
        using var reader = command.ExecuteReader();
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            int accountId = Int("Account");
            int usernameColumn = reader.GetOrdinal("SourceUsername");
            if (!accounts.TryGetValue(accountId, out var account) || reader.IsDBNull(usernameColumn) ||
                !string.Equals(account.Name, reader.GetString(usernameColumn), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SSC account {accountId} does not match an imported Vega account. Import accounts first.");
            if (payloads.Count == 100_000) throw new InvalidDataException("SSC import count exceeds the supported limit.");

            string inventory = reader.GetString(reader.GetOrdinal("Inventory"));
            if (inventory.Length > 65_536) throw new InvalidDataException($"SSC account {accountId} inventory exceeds the supported size.");
            ItemState[] slots = ParseInventory(inventory, accountId);
            int life = Int("Health"), maxLife = Int("MaxHealth"), mana = Int("Mana"), maxMana = Int("MaxMana");
            int loadout = Int("currentLoadoutIndex"), team = Int("team");
            int hideVisuals = Int("hideVisuals");
            int voiceColumn = reader.GetOrdinal("voicePitchOffset");
            float voice = reader.IsDBNull(voiceColumn) ? 0 : reader.GetFloat(voiceColumn);
            if (life < 0 || maxLife <= 0 || life > maxLife || mana < 0 || mana > maxMana || maxMana < 0 ||
                loadout is < 0 or > 2 || team is < 0 or > 5 || !float.IsFinite(voice))
                throw new InvalidDataException($"SSC account {accountId} has invalid character statistics.");

            var state = new CharacterState
            {
                Version = 1, Life = Math.Max(1, life), MaxLife = maxLife, Mana = mana, MaxMana = maxMana,
                SpawnX = Int("spawnX", -1), SpawnY = Int("spawnY", -1), Difficulty = difficulty,
                ExtraAccessory = Flag("extraSlot"), SkinVariant = Int("skinVariant"), Hair = Int("hair"),
                HairDye = checked((byte)Int("hairDye")),
                HairColor = Color("hairColor", new(92, 68, 48, 255)), PantsColor = Color("pantsColor", new(80, 90, 110, 255)),
                ShirtColor = Color("shirtColor", new(80, 130, 180, 255)), UnderShirtColor = Color("underShirtColor", new(160, 160, 160, 255)),
                ShoeColor = Color("shoeColor", new(80, 60, 45, 255)), SkinColor = Color("skinColor", new(255, 205, 148, 255)),
                EyeColor = Color("eyeColor", new(105, 90, 75, 255)),
                HideVisuals = Enumerable.Range(0, 10).Select(index => (hideVisuals & (1 << index)) != 0).ToArray(),
                QuestsCompleted = Int("questsCompleted"), UsingBiomeTorches = Flag("usingBiomeTorches"),
                HappyFunTorchTime = Flag("happyFunTorchTime"), UnlockedBiomeTorches = Flag("unlockedBiomeTorches"),
                CurrentLoadoutIndex = loadout, AteArtisanBread = Flag("ateArtisanBread"), UsedAegisCrystal = Flag("usedAegisCrystal"),
                UsedAegisFruit = Flag("usedAegisFruit"), UsedArcaneCrystal = Flag("usedArcaneCrystal"), UsedGalaxyPearl = Flag("usedGalaxyPearl"),
                UsedGummyWorm = Flag("usedGummyWorm"), UsedAmbrosia = Flag("usedAmbrosia"), UnlockedSuperCart = Flag("unlockedSuperCart"),
                EnabledSuperCart = Flag("enabledSuperCart"), DeathsPve = Int("deathsPVE"), DeathsPvp = Int("deathsPVP"),
                VoiceVariant = Int("voiceVariant"), VoicePitchOffset = voice, Team = team,
                Inventory = slots[..59], Armor = slots[59..79], Dye = slots[79..89], MiscEquips = slots[89..94], MiscDyes = slots[94..99],
                Piggy = slots[99..139], Safe = slots[139..179], Trash = slots[179], Forge = slots[180..220], VoidVault = slots[220..260],
                Loadouts = [new() { Armor = slots[260..280], Dye = slots[280..290] },
                    new() { Armor = slots[290..310], Dye = slots[310..320] }, new() { Armor = slots[320..340], Dye = slots[340..350] }]
            };
            if (!payloads.TryAdd(accountId.ToString(CultureInfo.InvariantCulture), SerializeImportedCharacter(state)))
                throw new InvalidDataException($"Duplicate SSC account {accountId}.");

            int Int(string name, int fallback = 0)
            {
                int index = reader.GetOrdinal(name);
                return reader.IsDBNull(index) ? fallback : reader.GetInt32(index);
            }
            bool Flag(string name) => Int(name) != 0;
            ColorState Color(string name, ColorState fallback)
            {
                int index = reader.GetOrdinal(name);
                if (reader.IsDBNull(index)) return fallback;
                uint value = unchecked((uint)reader.GetInt32(index));
                return new((byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
            }
        }
        return payloads;
    }

    private static ItemState[] ParseInventory(string inventory, int accountId)
    {
        string[] records = inventory.Split('~');
        if (records.Length != SlotCount)
            throw new InvalidDataException($"SSC account {accountId} uses an unsupported inventory layout ({records.Length} slots; expected {SlotCount}). Explicitly upgrade the source layout first.");
        var result = new ItemState[SlotCount];
        for (int index = 0; index < result.Length; index++)
        {
            string[] values = records[index].Split(',');
            if (values.Length is not (3 or 4) || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int item) ||
                !int.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stack) ||
                !byte.TryParse(values[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out byte prefix) ||
                item < 0 || item >= global::Terraria.ID.ItemID.Count || stack < 0 || stack > global::Terraria.Item.CommonMaxStack ||
                prefix >= TerrariaPacketSecurityLimits.PrefixCount || (item != 0 && stack == 0) || (item == 0 && (stack != 0 || prefix != 0)) ||
                (values.Length == 4 && values[3] is not ("0" or "1")))
                throw new InvalidDataException($"SSC account {accountId} has invalid item data at slot {index}.");
            result[index] = new(item, stack, prefix, values.Length == 4 && values[3] == "1");
        }
        return result;
    }
}
