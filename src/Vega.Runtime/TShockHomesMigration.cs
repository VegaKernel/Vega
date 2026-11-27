using System.Data;
using System.Globalization;
using Vega.Contracts.Accounts;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Runtime;

internal static class TShockHomesMigration
{
    public static IReadOnlyList<PlayerHome> Read(TShockMigrationSource source, int legacyWorldId, WorldId world, Guid worldFile)
    {
        using var connection = source.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AccountId, Name, X, Y FROM Homes WHERE WorldID = @world ORDER BY AccountId, Name;";
        TShockMigrationSource.AddParameter(command, "@world", DbType.Int32, legacyWorldId);
        using var reader = command.ExecuteReader();
        var homes = new List<PlayerHome>();
        while (reader.Read())
        {
            int account = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            string name = reader.GetString(1);
            float x = Convert.ToSingle(reader.GetValue(2), CultureInfo.InvariantCulture), y = Convert.ToSingle(reader.GetValue(3), CultureInfo.InvariantCulture);
            if (account <= 0 || !PlayerHome.IsValidName(name) || !float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || homes.Count == 100_000)
                throw new InvalidDataException("Homes source contains invalid data or exceeds the import limit.");
            homes.Add(new(new AccountId(account), world, worldFile, name, new(x, y)));
        }
        return homes.AsReadOnly();
    }
}
