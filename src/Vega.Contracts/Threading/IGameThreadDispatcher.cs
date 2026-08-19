namespace Vega.Contracts.Threading;

/// <summary>
/// Schedules work that must execute on Terraria's authoritative game thread.
/// </summary>
public interface IGameThreadDispatcher
{
    bool IsGameThreadKnown { get; }

    bool IsGameThread { get; }

    void AssertGameThread();

    bool TryPost(string name, Action action);

    bool TrySchedule(string name, Func<bool> step);
}
