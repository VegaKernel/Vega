using Vega.Core.Bans;
using Vega.Modules.Administration;
using Vega.Modules.Navigation;
using Vega.Persistence;
using Vega.Sandboxes;

namespace Vega.Runtime;

/// <summary>Explicit offline operator migrations; the normal Terraria host is never initialized.</summary>
internal static class OfflineMigrationCommands
{
    public static int? TryRun(string[] args, string baseDirectory)
    {
        if (args.Contains("--vega-import-tshock-whitelist", StringComparer.Ordinal))
        {
            if (args.Length != 3 || args[0] != "--vega-import-tshock-whitelist" || args[2] is not ("on" or "off"))
            {
                Console.Error.WriteLine("Usage: --vega-import-tshock-whitelist <TShock whitelist.txt> <on|off>. Stop all worlds first.");
                return 2;
            }
            try
            {
                if (new FileInfo(args[1]).Length > 1024 * 1024) throw new InvalidDataException("Whitelist source is too large.");
                var entries = new HashSet<string>(StringComparer.Ordinal);
                foreach (string line in File.ReadLines(args[1]))
                {
                    string value = line.Trim();
                    if (value.Length == 0 || value.StartsWith('#')) continue;
                    if (!ConnectionWhitelist.TryNormalizeEntry(value, out string normalized))
                        throw new InvalidDataException("Whitelist source contains an invalid IP or network.");
                    entries.Add(normalized);
                    if (entries.Count > 4096) throw new InvalidDataException("Whitelist import exceeds the entry limit.");
                }
                var whitelist = new ConnectionWhitelist(Path.Combine(baseDirectory, "data", "whitelist.json"));
                if (!whitelist.ImportAsync(entries, args[2] == "on").AsTask().GetAwaiter().GetResult())
                    throw new InvalidDataException("Destination whitelist is not empty and disabled; no existing entries were replaced.");
                Console.WriteLine($"Imported {entries.Count} shared whitelist IPs/networks; enabled={args[2] == "on"}.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                Console.Error.WriteLine($"Whitelist import failed: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--vega-import-tshock-regions", StringComparer.Ordinal))
        {
            if (args.Length != 4 || args[0] != "--vega-import-tshock-regions" ||
                !int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out int legacyWorldId))
            {
                Console.Error.WriteLine("Usage: --vega-import-tshock-regions <TShock SQLite database> <legacy numeric WorldID> <group mapping JSON>. Stop all worlds first.");
                return 2;
            }
            try
            {
                var database = OpenMigrationDatabase(args[1], "tshock-regions", baseDirectory);
                var regions = TShockRegionMigration.Read(args[1], legacyWorldId, args[3], database.Accounts.SnapshotAccounts());
                if (regions.Count == 0) { Console.Error.WriteLine("No regions matched the selected legacy WorldID. No regions were changed."); return 2; }
                int imported = database.ImportPrimaryRegionsAsync(regions).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"Imported {imported} regions into the primary world.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidCastException or OverflowException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Region import failed: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--vega-import-tshock-ssc", StringComparer.Ordinal))
        {
            if (args.Length != 3 || args[0] != "--vega-import-tshock-ssc" || args[2] is not ("classic" or "journey"))
            {
                Console.Error.WriteLine("Usage: --vega-import-tshock-ssc <TShock SQLite database> <classic|journey>. Import accounts and stop all worlds first.");
                return 2;
            }
            try
            {
                string data = Path.Combine(baseDirectory, "data");
                if (File.Exists(Path.Combine(data, "primary-characters.json")))
                    throw new InvalidDataException("Migrate existing Vega primary SSC before importing TShock characters.");
                var options = VegaDatabaseOptionsStore.Load(Path.Combine(baseDirectory, "config", "database.json"));
                if (options.ParseProvider() != VegaDatabaseProvider.Sqlite)
                    throw new InvalidDataException("This offline import currently supports a SQLite Vega account database only.");
                string target = Path.GetFullPath(options.SqlitePath, baseDirectory);
                if (!File.Exists(target) || string.Equals(target, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Import accounts into a separate Vega database first.");
                var database = VegaDatabaseFactory.Create(options, baseDirectory);
                database.InitializeAsync().AsTask().GetAwaiter().GetResult();
                var payloads = TShockCharacterMigration.Read(args[1], database.Accounts.SnapshotAccounts(), args[2] == "journey" ? (byte)3 : (byte)0);
                if (payloads.Count == 0) { Console.WriteLine("No TShock characters to import."); return 0; }
                var result = new SandboxSscStore(Path.Combine(data, "sandboxes", "ssc.json")).ImportPrimary(payloads, DateTimeOffset.UtcNow);
                Console.WriteLine($"TShock SSC: imported {result.Imported}, already present {result.AlreadyPresent}. Previous shared store: {result.SharedBackup ?? "(did not exist)"}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidCastException or OverflowException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"SSC import failed: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--vega-import-tshock-accounts", StringComparer.Ordinal))
        {
            if (args.Length != 3 || args[0] != "--vega-import-tshock-accounts")
            {
                Console.Error.WriteLine("Usage: --vega-import-tshock-accounts <TShock SQLite database> <explicit group mapping JSON>. Stop all worlds first.");
                return 2;
            }
            try
            {
                var rows = TShockAccountMigration.Read(args[1], args[2]);
                if (rows.Count == 0) { Console.WriteLine("No TShock accounts to import."); return 0; }
                var database = OpenMigrationDatabase(args[1], "tshock-accounts", baseDirectory);
                int imported = database.ImportAccountsAsync(rows).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"TShock accounts: imported {imported}, with original IDs and explicit target groups.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidCastException or OverflowException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"Account import failed: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--vega-import-tshock-bans", StringComparer.Ordinal))
        {
            if (args.Length != 2 || args[0] != "--vega-import-tshock-bans")
            {
                Console.Error.WriteLine("Usage: --vega-import-tshock-bans <TShock SQLite database>. Stop all worlds first.");
                return 2;
            }
            try
            {
                var bans = TShockBanMigration.Read(args[1], DateTimeOffset.UtcNow,
                    new GlobalBanService(new Vega.Cluster.Scenarios.Bans.InMemoryGlobalBanReplica()));
                if (bans.Count == 0) { Console.WriteLine("No active TShock bans to import."); return 0; }
                var database = OpenMigrationDatabase(args[1], "tshock-bans", baseDirectory);
                int imported = database.ImportBansAsync(bans).AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"TShock bans: imported {imported}, already present {bans.Count - imported}. Existing IDs were preserved.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidCastException or OverflowException)
            {
                Console.Error.WriteLine($"Ban import failed: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--vega-import-tshock-warps", StringComparer.Ordinal))
        {
            if (args.Length != 4 || args[0] != "--vega-import-tshock-warps" ||
                !int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out int legacyWorld) ||
                !Guid.TryParse(args[3], out Guid world) || world == Guid.Empty)
            {
                Console.Error.WriteLine("Usage: --vega-import-tshock-warps <TShock SQLite database> <legacy numeric WorldID> <Vega world file GUID>. Stop all worlds first.");
                return 2;
            }
            try
            {
                var points = TShockWarpMigration.Read(args[1], legacyWorld);
                if (points.Count == 0)
                {
                    Console.Error.WriteLine("No warps matched the selected legacy WorldID. Nothing was changed.");
                    return 2;
                }
                var store = new WarpStore(Path.Combine(baseDirectory, "data", "warps"));
                if (!store.ImportNewAsync(world, points).AsTask().GetAwaiter().GetResult())
                {
                    Console.Error.WriteLine("Destination warp store already exists. Nothing was overwritten.");
                    return 2;
                }
                Console.WriteLine($"Imported {points.Count} warps for world {world:D}.");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or InvalidCastException or OverflowException)
            {
                Console.Error.WriteLine($"Warp import failed: {ex.Message}");
                return 2;
            }
        }

        if (args.Contains("--vega-migrate-primary-ssc", StringComparer.Ordinal))
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("Run --vega-migrate-primary-ssc alone, with all Vega worlds stopped.");
                return 2;
            }
            try
            {
                string data = Path.Combine(baseDirectory, "data");
                var result = new SandboxSscStore(Path.Combine(data, "sandboxes", "ssc.json"))
                    .MigrateLegacyPrimary(Path.Combine(data, "primary-characters.json"), DateTimeOffset.UtcNow);
                Console.WriteLine($"SSC migration: imported {result.Imported}, already present {result.AlreadyPresent}. Legacy archive: {result.LegacyArchive}");
                Console.WriteLine($"Previous shared store: {result.SharedBackup ?? "(did not exist)"}");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
            {
                Console.Error.WriteLine($"SSC migration failed: {ex.Message}");
                return 2;
            }
        }
        return null;
    }

    private static Vega.Persistence.Sqlite.SqliteVegaDatabase OpenMigrationDatabase(string source, string migration, string baseDirectory)
    {
        var options = VegaDatabaseOptionsStore.Load(Path.Combine(baseDirectory, "config", "database.json"));
        if (options.ParseProvider() != VegaDatabaseProvider.Sqlite)
            throw new InvalidDataException("This offline import currently supports a SQLite Vega destination only.");
        string target = Path.GetFullPath(options.SqlitePath, baseDirectory);
        if (string.Equals(target, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TShock source and Vega destination must differ.");
        var database = (Vega.Persistence.Sqlite.SqliteVegaDatabase)VegaDatabaseFactory.Create(options, baseDirectory);
        bool existed = File.Exists(target);
        database.InitializeAsync().AsTask().GetAwaiter().GetResult();
        if (existed)
        {
            string backup = target + $".before-{migration}.{Guid.NewGuid():N}";
            database.WriteBackupAsync(backup).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"Previous Vega database: {backup}");
        }
        return database;
    }
}
