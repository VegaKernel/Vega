using Vega.Contracts.Extensions;

namespace Vega.Core.Extensions;

public sealed class ExtensionStateStore : IExtensionStateStore
{
    private readonly object sync = new();
    private readonly Dictionary<ExtensionStateScopeId, ExtensionStateScope> scopes = [];
    private bool disposed;

	public IExtensionStateScope CreateScope(ExtensionStateScopeId id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id.Kind);
		ArgumentException.ThrowIfNullOrWhiteSpace(id.Value);
		lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (scopes.ContainsKey(id))
            {
                throw new InvalidOperationException($"Extension state scope '{id}' already exists.");
            }

            var scope = new ExtensionStateScope(id);
            scopes.Add(id, scope);
            return scope;
        }
    }

    public bool TryGetScope(ExtensionStateScopeId id, out IExtensionStateScope? scope)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            bool found = scopes.TryGetValue(id, out ExtensionStateScope? concrete);
            scope = concrete;
            return found;
        }
    }

    public bool RemoveScope(ExtensionStateScopeId id)
    {
        ExtensionStateScope? scope;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!scopes.Remove(id, out scope))
            {
                return false;
            }
        }

        scope.Dispose();
        return true;
    }

    public void Dispose()
    {
        ExtensionStateScope[] remaining;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            remaining = scopes.Values.ToArray();
            scopes.Clear();
        }

        foreach (ExtensionStateScope scope in remaining)
        {
            scope.Dispose();
        }
    }

    private sealed class ExtensionStateScope(ExtensionStateScopeId id) : IExtensionStateScope
    {
        private readonly object sync = new();
        private readonly Dictionary<StateKey, object?> values = [];
        private bool disposed;

        public ExtensionStateScopeId Id { get; } = id;

        public T GetOrCreate<T>(ExtensionStateKey<T> key, Func<T> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            lock (sync)
            {
                ThrowIfDisposed();
                var identity = StateKey.From(key);
                if (values.TryGetValue(identity, out object? existing))
                {
                    return (T)existing!;
                }

                T created = factory();
                values.Add(identity, created);
                return created;
            }
        }

        public bool TryGet<T>(ExtensionStateKey<T> key, out T? value)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (values.TryGetValue(StateKey.From(key), out object? existing))
                {
                    value = (T)existing!;
                    return true;
                }

                value = default;
                return false;
            }
        }

        public void Set<T>(ExtensionStateKey<T> key, T value)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                values[StateKey.From(key)] = value;
            }
        }

        public bool Remove<T>(ExtensionStateKey<T> key)
        {
            lock (sync)
            {
                ThrowIfDisposed();
                return values.Remove(StateKey.From(key));
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                values.Clear();
            }
        }

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

        private readonly record struct StateKey(string Name, Type ValueType)
        {
            public static StateKey From<T>(ExtensionStateKey<T> key) => new(key.Name, typeof(T));
        }
    }
}
