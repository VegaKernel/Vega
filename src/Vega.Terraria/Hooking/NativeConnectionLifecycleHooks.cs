using Terraria.Net.Sockets;
using TerrariaRuntime;
using Vega.Terraria.Networking;

namespace Vega.Terraria.Hooking;

/// <summary>Native socket generation owns metadata lifetime, independently of player snapshot polling.</summary>
internal sealed class NativeConnectionLifecycleHooks(TerrariaConnectionMetadataRegistry metadata) : IDisposable
{
    private readonly Dictionary<int, ISocket> active = [];
    private bool attached;

    public void Attach()
    {
        if (attached) return;
        Hooks.Netplay.ServerConnectionActivated += OnActivated;
        Hooks.Netplay.ServerConnectionClosed += OnClosed;
        attached = true;
    }

    public void Dispose()
    {
        if (!attached) return;
        Hooks.Netplay.ServerConnectionActivated -= OnActivated;
        Hooks.Netplay.ServerConnectionClosed -= OnClosed;
        lock (active)
        {
            foreach (int slot in active.Keys) metadata.Remove(slot);
            active.Clear();
        }
        attached = false;
    }

    internal void OnActivated(int slot, ISocket socket)
    {
        lock (active)
        {
            active[slot] = socket;
            metadata.Remove(slot);
        }
    }

    internal void OnClosed(int slot, ISocket socket)
    {
        lock (active)
        {
            if (!active.TryGetValue(slot, out ISocket? current) || !ReferenceEquals(current, socket)) return;
            active.Remove(slot);
            metadata.Remove(slot);
        }
    }
}
