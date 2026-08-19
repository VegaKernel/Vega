using System;

namespace TShockAPI;

/// <summary>
/// Source-facing registration contract for legacy-shaped TShock events.
/// Event argument binary metadata is not a compatibility guarantee.
/// </summary>
public interface IHookRegistry<TEventArgs> where TEventArgs : EventArgs
{
    void Register(
        EventHandler<TEventArgs> handler,
        HandlerPriority priority = HandlerPriority.Normal,
        bool gethandled = false);

    void Unregister(EventHandler<TEventArgs> handler);
}
