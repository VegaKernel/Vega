using System.Data;
using Vega.Modules.Navigation;

namespace Vega.Runtime;

/// <summary>Read-only access to the pinned TShock warp schema; target world selection is explicit.</summary>
internal static class TShockWarpMigration
{
    public static IReadOnlyList<WarpPoint> Read(string sourcePath, int legacyWorldId)
        => Read(TShockMigrationSource.Parse(sourcePath), legacyWorldId);

    public static IReadOnlyList<WarpPoint> Read(TShockMigrationSource source, int legacyWorldId)
    {
        using var connection = source.OpenReadOnly();
        using var command = connection.CreateCommand();
        command.CommandText = source.IsMySql ? "SELECT WarpName, X, Y, `Private` FROM Warps WHERE WorldID = @world ORDER BY Id;" :
            "SELECT WarpName, X, Y, Private FROM Warps WHERE WorldID = @world ORDER BY Id;";
        TShockMigrationSource.AddParameter(command, "@world", DbType.String, legacyWorldId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        var points = new List<WarpPoint>();
        while (reader.Read())
        {
            // TShock stores center tile X and unmounted tile Y. Its standard 42px player is
            // positioned at bottom=(x*16+width/2, (y+3)*16), so the top-left is (x*16,y*16+6).
            string name = reader.GetString(0);
            int x = reader.GetInt32(1);
            int y = reader.GetInt32(2);
            if (x < 0 || y < 0 || x > 100_000 || y > 100_000 || points.Count == 4096)
                throw new InvalidDataException("TShock warp coordinates or count exceed import bounds.");
            bool hidden = !reader.IsDBNull(3) && reader.GetString(3) != "0";
            points.Add(new WarpPoint(name, new(x * 16f, y * 16f + 6f), hidden));
        }
        return points.AsReadOnly();
    }
}
