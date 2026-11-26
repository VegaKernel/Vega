using System.Text.Json;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Vega.Contracts.Accounts;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;
using Vega.Runtime;
using Vega.Sandboxes;
using static Vega.Terraria.Players.TerrariaSandboxSscCharacterAdapter;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class TShockCharacterMigrationTests
{
    [Test]
    public void MapsAll350SlotsAndAccountIdentityIntoSharedNativePayload()
    {
        string root = Path.Combine(Path.GetTempPath(), "vega-tshock-ssc-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "tshock.sqlite");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Pooling = false }.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE Users (ID INTEGER PRIMARY KEY, Username TEXT);
                    INSERT INTO Users VALUES (42,'Example');
                    CREATE TABLE tsCharacter (Account INTEGER PRIMARY KEY, Health INTEGER, MaxHealth INTEGER, Mana INTEGER, MaxMana INTEGER,
                    Inventory TEXT, extraSlot INTEGER, spawnX INTEGER, spawnY INTEGER, skinVariant INTEGER, hair INTEGER, hairDye INTEGER,
                    hairColor INTEGER, pantsColor INTEGER, shirtColor INTEGER, underShirtColor INTEGER, shoeColor INTEGER, hideVisuals INTEGER,
                    skinColor INTEGER, eyeColor INTEGER, questsCompleted INTEGER, usingBiomeTorches INTEGER, happyFunTorchTime INTEGER,
                    unlockedBiomeTorches INTEGER, currentLoadoutIndex INTEGER, ateArtisanBread INTEGER, usedAegisCrystal INTEGER,
                    usedAegisFruit INTEGER, usedArcaneCrystal INTEGER, usedGalaxyPearl INTEGER, usedGummyWorm INTEGER, usedAmbrosia INTEGER,
                    unlockedSuperCart INTEGER, enabledSuperCart INTEGER, deathsPVE INTEGER, deathsPVP INTEGER, voiceVariant INTEGER,
                    voicePitchOffset REAL, team INTEGER);
                    INSERT INTO tsCharacter (Account,Health,MaxHealth,Mana,MaxMana,Inventory,spawnX,spawnY,hairColor,hideVisuals,currentLoadoutIndex,usedAegisCrystal,team)
                    VALUES (42,140,400,60,200,@inventory,100,200,@color,5,2,1,3);
                    """;
                command.Parameters.AddWithValue("@inventory", string.Join("~", Enumerable.Range(1, 350).Select(item => $"{item},1,0,{(item == 180 ? 1 : 0)}")));
                command.Parameters.AddWithValue("@color", unchecked((int)0xFF1E140A));
                command.ExecuteNonQuery();
            }
            byte[] original = File.ReadAllBytes(source);
            AccountSnapshot[] accounts = [new(new AccountId(42), "example", "default", null)];
            var payloads = TShockCharacterMigration.Read(source, accounts, 0);
            var state = JsonSerializer.Deserialize<CharacterState>(payloads["42"], new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Multiple(() =>
            {
                Assert.That(state.Inventory.Select(item => item.Type), Is.EqualTo(Enumerable.Range(1, 59)));
                Assert.That(state.Armor.Select(item => item.Type), Is.EqualTo(Enumerable.Range(60, 20)));
                Assert.That(state.Dye.Select(item => item.Type), Is.EqualTo(Enumerable.Range(80, 10)));
                Assert.That(state.MiscEquips.Select(item => item.Type), Is.EqualTo(Enumerable.Range(90, 5)));
                Assert.That(state.MiscDyes.Select(item => item.Type), Is.EqualTo(Enumerable.Range(95, 5)));
                Assert.That(state.Piggy.Select(item => item.Type), Is.EqualTo(Enumerable.Range(100, 40)));
                Assert.That(state.Safe.Select(item => item.Type), Is.EqualTo(Enumerable.Range(140, 40)));
                Assert.That(state.Trash.Type, Is.EqualTo(180));
                Assert.That(state.Trash.Favorited, Is.True);
                Assert.That(state.Forge.Select(item => item.Type), Is.EqualTo(Enumerable.Range(181, 40)));
                Assert.That(state.VoidVault.Select(item => item.Type), Is.EqualTo(Enumerable.Range(221, 40)));
                Assert.That(state.Loadouts.SelectMany(loadout => loadout.Armor.Concat(loadout.Dye)).Select(item => item.Type), Is.EqualTo(Enumerable.Range(261, 90)));
                Assert.That((state.Life, state.MaxLife, state.Mana, state.MaxMana), Is.EqualTo((140, 400, 60, 200)));
                Assert.That(state.HairColor, Is.EqualTo(new ColorState(10, 20, 30, 255)));
                Assert.That(state.HideVisuals.Take(3), Is.EqualTo(new[] { true, false, true }));
                Assert.That(state.CurrentLoadoutIndex, Is.EqualTo(2));
                Assert.That(state.UsedAegisCrystal, Is.True);
                Assert.That(state.Team, Is.EqualTo(3));
                Assert.That(File.ReadAllBytes(source), Is.EqualTo(original));
            });
            var store = new SandboxSscStore(Path.Combine(root, "ssc.json"));
            Assert.That(store.ImportPrimary(payloads, DateTimeOffset.UtcNow).Imported, Is.EqualTo(1));
            Assert.That(store.Read("42", new WorldId("arena"), SandboxSscMode.Inherit)!.Payload, Is.EqualTo(payloads["42"]));
            Assert.That(store.ImportPrimary(payloads, DateTimeOffset.UtcNow).AlreadyPresent, Is.EqualTo(1));
            Assert.Throws<InvalidDataException>(() => TShockCharacterMigration.Read(source, [], 0));
            Assert.Throws<InvalidDataException>(() => TShockCharacterMigration.Read(source, [new(new AccountId(42), "other", "default", null)], 0));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
