using System.Reflection;
using System.Runtime.Loader;
using Vega.Contracts.Modules;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed class ServerPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver resolver;
    private readonly IReadOnlyDictionary<string, Assembly> sharedAssemblies;
    private readonly string pluginDirectory;

    public ServerPluginLoadContext(string pluginPath)
        : base($"Vega.ServerPlugin:{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        resolver = new AssemblyDependencyResolver(pluginPath);
        Assembly contractsAssembly = typeof(IHotReloadableVegaModule).Assembly;
        Assembly pluginSdkAssembly = typeof(VegaPlugin).Assembly;
        sharedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase)
        {
            [contractsAssembly.GetName().Name!] = contractsAssembly,
            [pluginSdkAssembly.GetName().Name!] = pluginSdkAssembly,
        };
        pluginDirectory = Path.GetDirectoryName(pluginPath)!;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null
            && sharedAssemblies.TryGetValue(assemblyName.Name, out Assembly? sharedAssembly))
        {
            return sharedAssembly;
        }

        string? path = resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null && assemblyName.Name is not null)
        {
            string adjacent = Path.Combine(pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(adjacent))
            {
                path = adjacent;
            }
        }
        if (path is null)
        {
            return null;
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return LoadFromStream(stream);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
