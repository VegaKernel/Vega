using System.Collections.Concurrent;
using Vega.Contracts.Worlds;

namespace Vega.Core.Events;

/// <summary>Per-world typed notification hub. Registration mutations never hold a lock during callbacks.</summary>
public sealed class WorldGameplayEventHub : IWorldGameplayEvents
{
    private readonly ConcurrentDictionary<Type, object> slots = new();

    public IDisposable Subscribe<T>(Action<T> handler) where T : struct
    {
        ArgumentNullException.ThrowIfNull(handler);
        return ((EventSlot<T>)slots.GetOrAdd(typeof(T), static _ => new EventSlot<T>())).Subscribe(handler);
    }

    public bool HasSubscribers<T>() where T : struct =>
        slots.TryGetValue(typeof(T), out object? slot) && ((EventSlot<T>)slot).HasSubscribers;

    public void Publish<T>(T value) where T : struct
    {
        if (slots.TryGetValue(typeof(T), out object? slot)) ((EventSlot<T>)slot).Publish(value);
    }

    public IDisposable SubscribePolicy<T>(Func<T, bool> allow) where T : struct
    {
        ArgumentNullException.ThrowIfNull(allow);
        return ((EventSlot<T>)slots.GetOrAdd(typeof(T), static _ => new EventSlot<T>())).SubscribePolicy(allow);
    }

    public bool HasPolicies<T>() where T : struct =>
        slots.TryGetValue(typeof(T), out object? slot) && ((EventSlot<T>)slot).HasPolicies;

    public bool Allows<T>(T value) where T : struct =>
        !slots.TryGetValue(typeof(T), out object? slot) || ((EventSlot<T>)slot).Allows(value);

    private sealed class EventSlot<T>
    {
        private readonly object sync = new();
        private Action<T>? handlers;
        private Func<T, bool>[] policies = [];
        public bool HasSubscribers => Volatile.Read(ref handlers) is not null;
        public bool HasPolicies => Volatile.Read(ref policies).Length != 0;
        public IDisposable Subscribe(Action<T> handler)
        {
            lock (sync) Volatile.Write(ref handlers, handlers + handler);
            return new Registration(this, handler);
        }
        public void Publish(T value) => Volatile.Read(ref handlers)?.Invoke(value);
        public IDisposable SubscribePolicy(Func<T, bool> allow)
        {
            lock (sync) Volatile.Write(ref policies, [.. policies, allow]);
            return new PolicyRegistration(this, allow);
        }
        public bool Allows(T value)
        {
            foreach (Func<T, bool> allow in Volatile.Read(ref policies)) if (!allow(value)) return false;
            return true;
        }
        private void RemovePolicy(Func<T, bool> allow)
        {
            lock (sync)
            {
                int index = Array.IndexOf(policies, allow);
                if (index >= 0) Volatile.Write(ref policies, [.. policies.AsSpan(0, index), .. policies.AsSpan(index + 1)]);
            }
        }
        private sealed class PolicyRegistration(EventSlot<T> slot, Func<T, bool> allow) : IDisposable
        {
            private EventSlot<T>? owner = slot;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.RemovePolicy(allow);
        }
        private void Remove(Action<T> handler)
        {
            lock (sync) Volatile.Write(ref handlers, handlers - handler);
        }
        private sealed class Registration(EventSlot<T> slot, Action<T> handler) : IDisposable
        {
            private EventSlot<T>? owner = slot;
            public void Dispose() => Interlocked.Exchange(ref owner, null)?.Remove(handler);
        }
    }
}
