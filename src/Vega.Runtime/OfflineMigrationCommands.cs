using Vega.Core.Bans;
using System.Data.Common;
using Vega.Modules.Administration;
using Vega.Modules.Navigation;
using Vega.Persistence;
using Vega.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Runtime;

/// <summary>Explicit offline operator migrations; the normal Terraria host is never initialized.</summary>
internal static class OfflineMigrationCommands
{
    private const string SourceUsage = "<TShock SQLite file|@mysql>";
    public static int? TryRun(string[] args, string baseDirectory)
    {
        if (args.Contains("--vega-import-tshock-homes", StringComparer.Ordinal)) return ImportHomes(args, baseDirectory);
        if (args.Contains("--vega-import-tshock-whitelist", StringComparer.Ordinal)) return ImportWhitelist(args, baseDirectory);
        if (args.Contains("--vega-import-tshock-regions", StringComparer.Ordinal)) return ImportRegions(args, baseDirectory);
        if (args.Contains("--vega-import-tshock-ssc", StringComparer.Ordinal)) return ImportSsc(args, baseDirectory);
        if (args.Contains("--vega-import-tshock-accounts", StringComparer.Ordinal)) return ImportAccounts(args, baseDirectory);
        if (args.Contains("--vega-import-tshock-bans", StringComparer.Ordinal)) return ImportBans(args, baseDirectory);
        if (args.Contains("--vega-import-tshock-warps", StringComparer.Ordinal)) return ImportWarps(args, baseDirectory);
        return null;
    }

    private static int ImportHomes(string[] args, string baseDirectory)
    {
        if (args.Length is < 4 or > 5 || args[0] != "--vega-import-tshock-homes" ||
            !int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out int legacyWorldId) ||
            !Guid.TryParse(args[3], out Guid worldFile) || worldFile == Guid.Empty)
        {
            Console.Error.WriteLine($"Usage: --vega-import-tshock-homes {SourceUsage} <legacy numeric WorldID> <world file GUID> [Vega world ID]. Import accounts and stop all worlds first.");
            return 2;
        }
        try
        {
            var source = TShockMigrationSource.Parse(args[1]);
            var database = OpenMigrationDatabase(source, "tshock-homes", baseDirectory, accountsOnly: true);
            WorldId world = args.Length == 5 ? new(args[4]) : WorldId.Primary;
            var points = TShockHomesMigration.Read(source, legacyWorldId, world, worldFile);
            if (points.Count == 0) { Console.WriteLine("No homes matched the selected legacy world."); return 0; }
            int imported = OfflineImporter(database).ImportHomesAsync(world, worldFile, points).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"Imported {imported} personal homes for {world} / {worldFile:D}.");
            return 0;
        }
        catch (Exception ex) when (IsTShockMigrationFailure(ex))
        {
            ReportMigrationFailure("Homes", ex);
            return 2;
        }
    }

    private static int ImportWhitelist(string[] args, string baseDirectory)
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

    private static int ImportRegions(string[] args, string baseDirectory)
    {
        if (args.Length != 4 || args[0] != "--vega-import-tshock-regions" ||
            !int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out int legacyWorldId))
        {
            Console.Error.WriteLine($"Usage: --vega-import-tshock-regions {SourceUsage} <legacy numeric WorldID> <group mapping JSON>. Stop all worlds first.");
            return 2;
        }
        try
        {
            var source = TShockMigrationSource.Parse(args[1]);
            var database = OpenMigrationDatabase(source, "tshock-regions", baseDirectory);
            var regions = TShockRegionMigration.Read(source, legacyWorldId, args[3], database.Accounts.SnapshotAccounts());
            if (regions.Count == 0) { Console.Error.WriteLine("No regions matched the selected legacy WorldID. No regions were changed."); return 2; }
            int imported = OfflineImporter(database).ImportPrimaryRegionsAsync(regions).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"Imported {imported} regions into the primary world.");
            return 0;
        }
        catch (Exception ex) when (IsTShockMigrationFailure(ex))
        {
            ReportMigrationFailure("Region", ex);
            return 2;
        }
    }

    private static int ImportSsc(string[] args, string baseDirectory)
    {
        if (args.Length != 3 || args[0] != "--vega-import-tshock-ssc" || args[2] is not ("classic" or "journey"))
        {
            Console.Error.WriteLine($"Usage: --vega-import-tshock-ssc {SourceUsage} <classic|journey>. Import accounts and stop all worlds first.");
            return 2;
        }
        try
        {
            string data = Path.Combine(baseDirectory, "data");
            if (File.Exists(Path.Combine(data, "primary-characters.json")))
                throw new InvalidDataException("A pre-release primary SSC file exists. Preserve it and export it with its matching development binary before importing TShock characters.");
            var source = TShockMigrationSource.Parse(args[1]);
            var database = OpenMigrationDatabase(source, "tshock-ssc", baseDirectory, accountsOnly: true);
            var payloads = TShockCharacterMigration.Read(source, database.Accounts.SnapshotAccounts(), args[2] == "journey" ? (byte)3 : (byte)0);
            if (payloads.Count == 0) { Console.WriteLine("No TShock characters to import."); return 0; }
            var result = new SandboxSscStore(Path.Combine(data, "sandboxes", "ssc.json")).ImportPrimary(payloads, DateTimeOffset.UtcNow);
            Console.WriteLine($"TShock SSC: imported {result.Imported}, already present {result.AlreadyPresent}. Previous shared store: {result.SharedBackup ?? "(did not exist)"}");
            return 0;
        }
        catch (Exception ex) when (IsTShockMigrationFailure(ex))
        {
            ReportMigrationFailure("SSC", ex);
            return 2;
        }
    }

    private static int ImportAccounts(string[] args, string baseDirectory)
    {
        if (args.Length != 3 || args[0] != "--vega-import-tshock-accounts")
        {
            Console.Error.WriteLine($"Usage: --vega-import-tshock-accounts {SourceUsage} <explicit group mapping JSON>. Stop all worlds first.");
            return 2;
        }
        try
        {
            var source = TShockMigrationSource.Parse(args[1]);
            var rows = TShockAccountMigration.Read(source, args[2]);
            if (rows.Count == 0) { Console.WriteLine("No TShock accounts to import."); return 0; }
            var database = OpenMigrationDatabase(source, "tshock-accounts", baseDirectory);
            int imported = OfflineImporter(database).ImportAccountsAsync(rows).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"TShock accounts: imported {imported}, with original IDs and explicit target groups.");
            return 0;
        }
        catch (Exception ex) when (IsTShockMigrationFailure(ex))
        {
            ReportMigrationFailure("Account", ex);
            return 2;
        }
    }

    private static int ImportBans(string[] args, string baseDirectory)
    {
        if (args.Length != 2 || args[0] != "--vega-import-tshock-bans")
        {
            Console.Error.WriteLine($"Usage: --vega-import-tshock-bans {SourceUsage}. Stop all worlds first.");
            return 2;
        }
        try
        {
            var source = TShockMigrationSource.Parse(args[1]);
            var bans = TShockBanMigration.Read(source, DateTimeOffset.UtcNow,
                new GlobalBanService(new Vega.Cluster.Scenarios.Bans.InMemoryGlobalBanReplica()));
            if (bans.Count == 0) { Console.WriteLine("No active TShock bans to import."); return 0; }
            var database = OpenMigrationDatabase(source, "tshock-bans", baseDirectory);
            int imported = OfflineImporter(database).ImportBansAsync(bans).AsTask().GetAwaiter().GetResult();
            Console.WriteLine($"TShock bans: imported {imported}, already present {bans.Count - imported}. Existing IDs were preserved.");
            return 0;
        }
        catch (Exception ex) when (IsTShockMigrationFailure(ex))
        {
            ReportMigrationFailure("Ban", ex);
            return 2;
        }
    }

    private static int ImportWarps(string[] args, string baseDirectory)
    {
        if (args.Length != 4 || args[0] != "--vega-import-tshock-warps" ||
            !int.TryParse(args[2], System.Globalization.CultureInfo.InvariantCulture, out int legacyWorld) ||
            !Guid.TryParse(args[3], out Guid world) || world == Guid.Empty)
        {
            Console.Error.WriteLine($"Usage: --vega-import-tshock-warps {SourceUsage} <legacy numeric WorldID> <Vega world file GUID>. Stop all worlds first.");
            return 2;
        }
        try
        {
            var points = TShockWarpMigration.Read(TShockMigrationSource.Parse(args[1]), legacyWorld);
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
        catch (Exception ex) when (IsTShockMigrationFailure(ex))
        {
            ReportMigrationFailure("Warp", ex);
            return 2;
        }
    }

    private static IVegaDatabase OpenMigrationDatabase(TShockMigrationSource source, string migration, string baseDirectory, bool accountsOnly = false)
    {
        var options = VegaDatabaseOptionsStore.Load(Path.Combine(baseDirectory, "config", "database.json"));
        VegaDatabaseProvider provider = options.ParseProvider();
        string? target = provider == VegaDatabaseProvider.Sqlite ? Path.GetFullPath(options.SqlitePath, baseDirectory) : null;
        if (target is not null && source.SqlitePath is { } sourcePath &&
            string.Equals(CanonicalPath(target), CanonicalPath(sourcePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("TShock source and Vega destination must differ.");
        if (provider == VegaDatabaseProvider.MySql)
            source.EnsureDifferentMySqlDestination(options.ResolveExternalConnectionString());
        if (accountsOnly && target is not null && !File.Exists(target))
            throw new InvalidDataException("Import accounts into a separate Vega database first.");
        var database = VegaDatabaseFactory.Create(options, baseDirectory);
        bool existed = target is null || File.Exists(target);
        database.InitializeAsync().AsTask().GetAwaiter().GetResult();
        if (existed && !accountsOnly)
        {
            string backup = target is not null ? target + $".before-{migration}.{Guid.NewGuid():N}"
                : Path.Combine(baseDirectory, "data", "migration-backups", $"{migration}.{Guid.NewGuid():N}", database.BackupFileName);
            try
            {
                database.WriteBackupAsync(backup).AsTask().GetAwaiter().GetResult();
                if (!File.Exists(backup) || new FileInfo(backup).Length == 0)
                    throw new IOException("Destination backup was empty; no import rows were written.");
            }
            catch (Exception ex) when (target is null && IsTShockMigrationFailure(ex))
            {
                // External driver/dump diagnostics can include connection identity. No imported rows
                // have been written, and connection strings must never become console output.
                throw new InvalidDataException("Unable to back up the Vega destination. Verify the configured database, dump tool and backup permissions; no import rows were written.");
            }
            Console.WriteLine($"Previous Vega database: {backup}");
        }
        return database;
    }

    private static string CanonicalPath(string path) => File.Exists(path)
        ? new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path)
        : Path.GetFullPath(path);

    private static IVegaOfflineImport OfflineImporter(IVegaDatabase database) => database as IVegaOfflineImport
        ?? throw new InvalidDataException("The configured Vega database does not support offline import.");

    private static bool IsTShockMigrationFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or InvalidDataException
        or DbException or ArgumentException or InvalidCastException or OverflowException or FormatException or System.Text.Json.JsonException or InvalidOperationException;

    private static void ReportMigrationFailure(string operation, Exception ex) => Console.Error.WriteLine($"{operation} import failed: " +
        (ex is DbException or ArgumentException or InvalidOperationException or FormatException
            ? "Invalid migration request or database operation failed. Verify the provider, schema, connection and source values."
            : ex.Message));
}
