using System.Globalization;
using Terraria.ID;
using Vega.Contracts.Items;

namespace Vega.Terraria.Items;

/// <summary>
/// Resolves Terraria item IDs and localized display names without exposing Terraria item types to callers.
/// Resolution prefers exact names, then normalized exact names, then unique prefixes.
/// </summary>
public sealed class TerrariaItemCatalog : IItemCatalog
{
    private const int MaximumCandidates = 8;

    public ItemResolutionResult Resolve(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        string value = query.Trim();

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int itemType))
        {
            return IsValidType(itemType)
                ? Found(itemType)
                : new ItemResolutionResult(ItemResolutionStatus.NotFound);
        }

        string normalizedQuery = Normalize(value);
        var exact = new List<int>();
        var normalizedExact = new List<int>();
        var prefixes = new List<int>();

        for (int type = 1; type < ItemID.Count; type++)
        {
            string name = global::Terraria.Lang.GetItemNameValue(type);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(type);
                continue;
            }

            string normalizedName = Normalize(name);
            if (normalizedName == normalizedQuery)
            {
                normalizedExact.Add(type);
            }
            else if (normalizedName.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                prefixes.Add(type);
            }
        }

        return ResolveTier(exact) ??
            ResolveTier(normalizedExact) ??
            ResolveTier(prefixes) ??
            new ItemResolutionResult(ItemResolutionStatus.NotFound);
    }

    private static ItemResolutionResult? ResolveTier(List<int> matches)
    {
        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Count == 1)
        {
            return Found(matches[0]);
        }

        return new ItemResolutionResult(
            ItemResolutionStatus.Ambiguous,
            Matches: matches
                .Take(MaximumCandidates)
                .Select(Definition)
                .ToArray());
    }

    private static ItemResolutionResult Found(int type) =>
        new(ItemResolutionStatus.Found, Definition(type));

    private static ItemDefinition Definition(int type)
    {
        var item = new global::Terraria.Item();
        item.SetDefaults(type);
        return new ItemDefinition(
            type,
            global::Terraria.Lang.GetItemNameValue(type),
            Math.Max(1, item.maxStack));
    }

    private static bool IsValidType(int type) => type > 0 && type < ItemID.Count;

    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 256
            ? stackalloc char[value.Length]
            : new char[value.Length];
        int written = 0;
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[written++] = char.ToUpperInvariant(character);
            }
        }

        return new string(buffer[..written]);
    }
}
