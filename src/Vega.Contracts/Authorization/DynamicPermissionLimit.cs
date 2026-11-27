namespace Vega.Contracts.Authorization;

/// <summary>Resolves a numeric permission family, honoring the effective permission evaluator (including denies).</summary>
public static class DynamicPermissionLimit
{
    public static int Resolve(string root, IEnumerable<string> knownPermissions, Func<string, bool> hasPermission, int fallback = 0)
    {
        if (hasPermission(root + ".*")) return int.MaxValue;
        int limit = hasPermission(root) ? Math.Max(1, fallback) : fallback;
        foreach (string permission in knownPermissions)
            if (permission.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(permission.AsSpan(root.Length + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int amount) && amount > limit && hasPermission(permission))
                limit = amount;
        return limit;
    }
}
