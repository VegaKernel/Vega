using Vega.Contracts.Runtime;

namespace Vega.TerminalUI;

/// <summary>
/// Prevents Terminal.Gui from taking ownership of the console while Terraria is
/// still using standard input for its interactive world-selection bootstrap.
/// Creating a world in the native menu emits WorldLoading, then returns to that menu.
/// </summary>
public sealed class TerminalUiReadinessGate : IDisposable
{
    private readonly object sync = new();
    private readonly IRuntimeLifecycle lifecycle;
    private readonly OperationsUiMode mode;
    private readonly RuntimeLifecycleStage startAt;
    private readonly Func<OperationsUiMode, bool> start;
    private bool subscribed;
    private bool started;
    private bool disposed;

    public TerminalUiReadinessGate(
        IRuntimeLifecycle lifecycle,
        OperationsUiMode mode,
        bool worldSelectedAtLaunch,
        Func<OperationsUiMode, bool> start)
    {
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.mode = mode;
        startAt = worldSelectedAtLaunch ? RuntimeLifecycleStage.WorldLoading : RuntimeLifecycleStage.WorldReady;
        this.start = start ?? throw new ArgumentNullException(nameof(start));

        if (mode != OperationsUiMode.Terminal)
        {
            return;
        }

        subscribed = true;
        lifecycle.Transitioned += OnLifecycleTransitioned;
        TryStart(lifecycle.CurrentStage);
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
            Unsubscribe();
        }
    }

    private void OnLifecycleTransitioned(RuntimeLifecycleTransition transition) =>
        TryStart(transition.Stage);

    private void TryStart(RuntimeLifecycleStage stage)
    {
        lock (sync)
        {
            if (disposed || started || stage < startAt || stage >= RuntimeLifecycleStage.Stopping)
            {
                return;
            }

            started = true;
            Unsubscribe();
        }

        start(mode);
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        lifecycle.Transitioned -= OnLifecycleTransitioned;
        subscribed = false;
    }
}
