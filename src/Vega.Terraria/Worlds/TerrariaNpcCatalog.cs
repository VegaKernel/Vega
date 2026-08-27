using System.Globalization;
using Terraria.ID;
using Vega.Contracts.Worlds;

namespace Vega.Terraria.Worlds;

public sealed class TerrariaNpcCatalog : INpcCatalog
{
    private const int MaximumCandidates = 8;

    public NpcResolutionResult Resolve(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        string value = query.Trim();
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int netId))
        {
            return IsValid(netId)
                ? Found(netId)
                : new NpcResolutionResult(NpcResolutionStatus.NotFound);
        }

        var exact = new List<int>();
        var prefixes = new List<int>();
        var contains = new List<int>();
        for (int id = 1; id < NPCID.Count; id++)
        {
            string name = global::Terraria.Lang.GetNPCNameValue(id);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(id);
            }
            else if (name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                prefixes.Add(id);
            }
            else if (name.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(id);
            }
        }

        return ResolveTier(exact) ??
            ResolveTier(prefixes.Count == 1 ? prefixes : [.. prefixes, .. contains]) ??
            new NpcResolutionResult(NpcResolutionStatus.NotFound);
    }

    private static NpcResolutionResult? ResolveTier(List<int> matches)
    {
        int[] distinct = matches.Distinct().ToArray();
        return distinct.Length switch
        {
            0 => null,
            1 => Found(distinct[0]),
            _ => new NpcResolutionResult(
                NpcResolutionStatus.Ambiguous,
                Matches: distinct.Take(MaximumCandidates).Select(Definition).ToArray())
        };
    }

    private static NpcResolutionResult Found(int netId) =>
        new(NpcResolutionStatus.Found, Definition(netId));

    private static NpcDefinition Definition(int netId) =>
        new(netId, global::Terraria.Lang.GetNPCNameValue(netId));

    private static bool IsValid(int netId) => netId >= 0 && netId < NPCID.Count;
}
