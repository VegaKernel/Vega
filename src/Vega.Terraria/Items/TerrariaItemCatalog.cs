using System.Globalization;
using System.Text.RegularExpressions;
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
    private static readonly Regex ItemTagPattern = new(
        @"\[i(?:tem)?(?:/s\d{1,4})?(?:/p\d{1,3})?:(?<type>-?\d{1,4})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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

        Match tag = ItemTagPattern.Match(value);
        if (tag.Success &&
            int.TryParse(tag.Groups["type"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemType))
        {
            return IsValidType(itemType)
                ? Found(itemType)
                : new ItemResolutionResult(ItemResolutionStatus.NotFound);
        }

        string normalizedQuery = Normalize(value);
        if (normalizedQuery.Length == 0)
        {
            return new ItemResolutionResult(ItemResolutionStatus.NotFound);
        }

        var exact = new List<int>();
        var normalizedExact = new List<int>();
        var prefixes = new List<int>();
        var contains = new List<int>();

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
            else if (normalizedName.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(type);
            }
        }

        return ResolveTier(exact) ??
            ResolveTier(normalizedExact) ??
            ResolveTier(prefixes.Count == 1 ? prefixes : [.. prefixes, .. contains]) ??
            new ItemResolutionResult(ItemResolutionStatus.NotFound);
    }

    public ItemPrefixResolutionResult ResolvePrefix(string query, ItemDefinition item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(item);
        string value = query.Trim();

        if (byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte prefixId))
        {
            return IsValidPrefix(prefixId)
                ? FoundPrefix(item.IsAccessory && prefixId == PrefixID.Quick
                    ? (byte)PrefixID.Quick2
                    : prefixId)
                : new ItemPrefixResolutionResult(ItemResolutionStatus.NotFound);
        }

        var exact = new List<byte>();
        var prefixes = new List<byte>();
        var contains = new List<byte>();
        for (int id = 1; id < PrefixID.Count && id <= byte.MaxValue; id++)
        {
            string name = global::Terraria.Lang.prefix[id].ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            byte candidate = (byte)id;
            if (string.Equals(name, value, StringComparison.OrdinalIgnoreCase))
            {
                exact.Add(candidate);
            }
            else if (name.StartsWith(value, StringComparison.OrdinalIgnoreCase))
            {
                prefixes.Add(candidate);
            }
            else if (name.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                contains.Add(candidate);
            }
        }

        return ResolvePrefixTier(exact, item) ??
            ResolvePrefixTier(prefixes.Count == 1 ? prefixes : [.. prefixes, .. contains], item) ??
            new ItemPrefixResolutionResult(ItemResolutionStatus.NotFound);
    }

    private static ItemResolutionResult? ResolveTier(List<int> matches)
    {
        matches = matches.Distinct().ToList();
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

    private static ItemPrefixResolutionResult? ResolvePrefixTier(
        List<byte> matches,
        ItemDefinition item)
    {
        List<byte> normalized = matches.Distinct().ToList();
        if (normalized.Contains((byte)PrefixID.Quick))
        {
            normalized.Remove((byte)PrefixID.Quick2);
            if (item.IsAccessory)
            {
                normalized.Remove((byte)PrefixID.Quick);
                normalized.Add((byte)PrefixID.Quick2);
            }
        }

        return normalized.Count switch
        {
            0 => null,
            1 => FoundPrefix(normalized[0]),
            _ => new ItemPrefixResolutionResult(
                ItemResolutionStatus.Ambiguous,
                Matches: normalized
                    .Take(MaximumCandidates)
                    .Select(PrefixDefinition)
                    .ToArray())
        };
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
            Math.Max(1, item.maxStack),
            item.OnlyNeedOneInInventory(),
            item.accessory);
    }

    private static bool IsValidType(int type) => type > 0 && type < ItemID.Count;

    private static bool IsValidPrefix(byte prefix) => prefix > 0 && prefix < PrefixID.Count;

    private static ItemPrefixResolutionResult FoundPrefix(byte prefix) =>
        new(ItemResolutionStatus.Found, PrefixDefinition(prefix));

    private static ItemPrefixDefinition PrefixDefinition(byte prefix) =>
        new(prefix, global::Terraria.Lang.prefix[prefix].ToString() ?? prefix.ToString(CultureInfo.InvariantCulture));

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
