using System.Reflection;
using TShockNG.Contracts.Authorization;
using TShockNG.Contracts.Players;

namespace Vega.Terraria.Authorization;

/// <summary>
/// Transitional player-authorization adapter backed by the legacy TShockAPI session objects.
/// Remove this type when Vega owns login/session authorization state directly.
/// </summary>
public sealed class LegacyTShockPlayerAuthorizationService : IPlayerAuthorizationService
{
    private readonly object sync = new();
    private Accessors? accessors;

    public bool HasPermission(PlayerId playerId, string permission)
    {
        if (playerId.Value < 0 || string.IsNullOrWhiteSpace(permission))
        {
            return false;
        }

        Accessors? resolved = accessors ?? ResolveAccessors();
        if (resolved is null)
        {
            return false;
        }

        try
        {
            if (resolved.PlayersField.GetValue(null) is not Array players || playerId.Value >= players.Length)
            {
                return false;
            }

            object? player = players.GetValue(playerId.Value);
            return player is not null && resolved.HasPermissionMethod.Invoke(player, [permission]) is true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Legacy TShock authorization lookup failed for slot {playerId.Value}: {ex.Message}");
            return false;
        }
    }

    private Accessors? ResolveAccessors()
    {
        lock (sync)
        {
            if (accessors is not null)
            {
                return accessors;
            }

            Assembly? assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(static assembly =>
                string.Equals(assembly.GetName().Name, "TShockAPI", StringComparison.Ordinal));
            if (assembly is null)
            {
                return null;
            }

            Type? tshockType = assembly.GetType("TShockAPI.TShock", throwOnError: false);
            Type? playerType = assembly.GetType("TShockAPI.TSPlayer", throwOnError: false);
            FieldInfo? playersField = tshockType?.GetField(
                "Players",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo? hasPermissionMethod = playerType?.GetMethod(
                "HasPermission",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(string)],
                modifiers: null);

            if (playersField is null || hasPermissionMethod is null)
            {
                return null;
            }

            accessors = new Accessors(playersField, hasPermissionMethod);
            return accessors;
        }
    }

    private sealed record Accessors(FieldInfo PlayersField, MethodInfo HasPermissionMethod);
}
