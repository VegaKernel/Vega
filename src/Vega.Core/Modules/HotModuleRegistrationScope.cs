using Vega.Contracts.Commands;
using Vega.Contracts.Modules;

namespace Vega.Core.Modules;

internal sealed class HotModuleRegistrationScope(ICommandRegistry commands) : IHotModuleContext, IDisposable
{
    private readonly object sync = new();
    private readonly List<IDisposable> registrations = [];
    private bool disposed;

    public IDisposable RegisterCommand(ICommandHandler handler) => Track(commands.Register(handler));

    public T Track<T>(T registration) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            registrations.Add(registration);
            return registration;
        }
    }

    public IDisposable RegisterCleanup(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return Track(new CleanupRegistration(cleanup));
    }

    public void Dispose()
    {
        IDisposable[] owned;
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            owned = registrations.ToArray();
            registrations.Clear();
        }

        List<Exception>? failures = null;
        for (var index = owned.Length - 1; index >= 0; index--)
        {
            try
            {
                owned[index].Dispose();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more hot-module registrations failed to detach.", failures);
        }
    }

    private sealed class CleanupRegistration(Action cleanup) : IDisposable
    {
        private Action? cleanup = cleanup;

        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }
}
