using System.Diagnostics;
using System.Reflection;
using TShockPluginManager;
using Vega.Contracts.Events;
using Vega.Contracts.Worlds;
using Vega.Core.Events;
using Vega.Terraria;

var processStartedAt = Stopwatch.GetTimestamp();

if (File.Exists("TerrariaServer.exe"))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine("A TerrariaServer.exe file was found in the current working directory.");
    Console.Error.WriteLine("Vega must run from its own server directory, not from a Terraria client or legacy server installation.");
    Console.ResetColor();
    return 1;
}

var assemblyCache = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += ResolveFromBin;

return await StartAsync();

Assembly? ResolveFromBin(System.Runtime.Loader.AssemblyLoadContext context, AssemblyName assemblyName)
{
    if (assemblyName.Name is null)
    {
        return null;
    }

    if (assemblyCache.TryGetValue(assemblyName.Name, out var cached))
    {
        return cached;
    }

    var path = Path.Combine(AppContext.BaseDirectory, "bin", assemblyName.Name + ".dll");
    Assembly? assembly = null;

    if (File.Exists(path))
    {
        assembly = context.LoadFromAssemblyPath(path);
    }
    else
    {
        path = Path.ChangeExtension(path, ".exe");
        if (File.Exists(path))
        {
            assembly = context.LoadFromAssemblyPath(path);
        }
    }

    if (assembly is not null)
    {
        assemblyCache[assemblyName.Name] = assembly;
    }

    return assembly;
}

async Task<int> StartAsync()
{
    if (args.Length > 0 && string.Equals(args[0], "plugins", StringComparison.OrdinalIgnoreCase))
    {
        await NugetCLI.Main(args.Skip(1).ToList());
        return 0;
    }

    // Explicit composition root: no assembly scanning or reflection-based handler discovery.
    IEventPipeline<WorldHardmodeStarting> hardmodeStarting = new EventPipeline<WorldHardmodeStarting>(
        Array.Empty<IEventHandler<WorldHardmodeStarting>>(),
        Array.Empty<IEventMonitor<WorldHardmodeStarting>>());

    return TerrariaServerHost.Run(args, processStartedAt, hardmodeStarting);
}
