using Vega.Contracts.Sandboxes;

namespace Vega.PluginSdk;

/// <summary>
/// Process-local bridge used by the Vega host to publish the single sandbox control-plane instance
/// to plugin scopes without exposing a general service locator.
/// </summary>
internal static class VegaPluginSandboxRuntime
{
    private static readonly object Sync = new();
    private static RegistrationState? current;

    internal static ISandboxDirectory? Directory
    {
        get
        {
            lock (Sync)
            {
                return current?.Directory;
            }
        }
    }

    internal static ISandboxControlService? Control
    {
        get
        {
            lock (Sync)
            {
                return current?.Control;
            }
        }
    }

    internal static IDisposable Register(
        ISandboxDirectory directory,
        ISandboxControlService? control = null)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var state = new RegistrationState(directory, control);
        lock (Sync)
        {
            current = state;
        }

        return new Registration(state);
    }

    private sealed record RegistrationState(
        ISandboxDirectory Directory,
        ISandboxControlService? Control);

    private sealed class Registration(RegistrationState state) : IDisposable
    {
        private RegistrationState? state = state;

        public void Dispose()
        {
            RegistrationState? owned = Interlocked.Exchange(ref state, null);
            if (owned is null)
            {
                return;
            }

            lock (Sync)
            {
                if (ReferenceEquals(current, owned))
                {
                    current = null;
                }
            }
        }
    }
}
