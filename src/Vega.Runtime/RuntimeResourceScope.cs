using System.Runtime.ExceptionServices;

namespace Vega.Runtime;

/// <summary>Owns resources across startup phases, preserving nested-using unwind order even on a failed phase.</summary>
internal sealed class RuntimeResourceScope : IDisposable
{
    private readonly Stack<IDisposable> resources = new();
    private bool disposed;

    internal T Own<T>(T resource) where T : IDisposable?
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resource is not null) resources.Push(resource);
        return resource;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ExceptionDispatchInfo? failure = null;
        while (resources.TryPop(out var resource))
        {
            try { resource.Dispose(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        }
        failure?.Throw();
    }
}
