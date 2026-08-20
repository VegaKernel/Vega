using System.Reflection;
using TShockNG.Contracts.Updates;

namespace Vega.Terraria.Updates;

/// <summary>
/// Transitional update-check adapter backed by the legacy TShock update manager.
/// Remove this type when Vega owns update discovery and administrator notification.
/// </summary>
public sealed class LegacyTShockUpdateChecker : IUpdateChecker
{
    private readonly object sync = new();
    private Accessors? accessors;

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Accessors resolved = accessors ?? ResolveAccessors() ??
            throw new InvalidOperationException("The legacy TShock update manager is unavailable.");
        object updateManager = resolved.UpdateManagerField.GetValue(null) ??
            throw new InvalidOperationException("The legacy TShock update manager is not initialized.");
        object? invocation = resolved.UpdateCheckMethod.Invoke(updateManager, [null]);
        if (invocation is not Task updateCheck)
        {
            throw new InvalidOperationException("The legacy TShock update check did not return a task.");
        }

        await updateCheck.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            Type? tshockType = assembly?.GetType("TShockAPI.TShock", throwOnError: false);
            FieldInfo? updateManagerField = tshockType?.GetField(
                "UpdateManager",
                BindingFlags.Public | BindingFlags.Static);
            MethodInfo? updateCheckMethod = updateManagerField?.FieldType.GetMethod(
                "UpdateCheckAsync",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(object)],
                modifiers: null);

            if (updateManagerField is null || updateCheckMethod is null)
            {
                return null;
            }

            accessors = new Accessors(updateManagerField, updateCheckMethod);
            return accessors;
        }
    }

    private sealed record Accessors(
        FieldInfo UpdateManagerField,
        MethodInfo UpdateCheckMethod);
}
