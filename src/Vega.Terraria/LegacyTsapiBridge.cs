using System.Reflection;
using TerrariaApi.Server;

namespace Vega.Terraria;

/// <summary>
/// Temporary containment boundary for the remaining TSAPI hook pipeline.
/// Remove this type when Vega.Terraria owns OTAPI hook registration and shutdown directly.
/// </summary>
internal static class LegacyTsapiBridge
{
    private static readonly MethodInfo? DeInitializeMethod = typeof(ServerApi).GetMethod(
        "DeInitialize",
        BindingFlags.Static | BindingFlags.NonPublic);

    public static void AttachHooks(string[] args)
    {
        ServerApi.Hooks.AttachOTAPIHooks(args);
    }

    public static void Shutdown()
    {
        try
        {
            DeInitializeMethod?.Invoke(null, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            Console.Error.WriteLine($"Legacy TSAPI shutdown failed: {ex.InnerException}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Legacy TSAPI shutdown failed: {ex}");
        }
    }
}
