using Vega.Contracts.Operations;
using Vega.Contracts.Threading;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginTasks(HotModuleRegistrationScope owner, IGameThreadDispatcher? gameThread) : IPluginTasks
    {
        // One Vega native world per process. Reserve a core for its authoritative
        // simulation, bound admitted work globally and per activation.
        private static readonly SemaphoreSlim CpuLanes = new(Math.Clamp(Environment.ProcessorCount - 1, 1, 4));
        private const int WorldCpuCapacity = 32;
        private const int PluginCpuCapacity = 4;
        private static int worldCpuPending;
        private int pluginCpuPending;

        public bool Post(string name, Action action)
        {
            Validate(name, action);
            if (!owner.executionGuard.AcceptsCallbacks) return false;
            return gameThread?.TryPost(name, () =>
            {
                if (!owner.lifetime.IsCancellationRequested)
                {
                    owner.executionGuard.TryInvoke($"task:{name}", action);
                }
            }) ?? false;
        }

        public void Every(string name, TimeSpan interval, Action action)
        {
            Validate(name, action);
            if (interval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be positive.");
            }

            owner.executionGuard.TryInvoke($"schedule:{name}", () => owner.TrackTask(() => RepeatAsync(name, interval, action)));
        }

        public void RunIo<T>(string name, Func<CancellationToken, ValueTask<T>> operation, Action<T> apply)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(apply);
            owner.executionGuard.TryInvoke($"schedule:{name}", () => owner.TrackTask(() => Task.Run(() => RunIoCoreAsync(name, operation, apply))));
        }

        public bool TryRunCpu<T>(string name, Func<CancellationToken, T> compute, Action<T> apply)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(compute);
            ArgumentNullException.ThrowIfNull(apply);
            if (gameThread is null || !owner.executionGuard.AcceptsCallbacks) return false;
            bool accepted = false;
            owner.executionGuard.TryInvoke($"schedule:{name}", () =>
            {
                if (!Reserve(ref pluginCpuPending, PluginCpuCapacity)) return;
                if (!Reserve(ref worldCpuPending, WorldCpuCapacity)) { Interlocked.Decrement(ref pluginCpuPending); return; }
                try
                {
                    owner.TrackTask(() => Task.Run(() => RunCpuCoreAsync(name, compute, apply)));
                    accepted = true;
                }
                catch
                {
                    Interlocked.Decrement(ref worldCpuPending);
                    Interlocked.Decrement(ref pluginCpuPending);
                    throw;
                }
            });
            return accepted;
        }

        private static bool Reserve(ref int pending, int capacity)
        {
            if (Interlocked.Increment(ref pending) <= capacity) return true;
            Interlocked.Decrement(ref pending);
            return false;
        }

        private async Task RunCpuCoreAsync<T>(string name, Func<CancellationToken, T> compute, Action<T> apply)
        {
            CancellationToken token = owner.lifetime.Token;
            try
            {
                await CpuLanes.WaitAsync(token).ConfigureAwait(false);
                T result;
                try { token.ThrowIfCancellationRequested(); result = compute(token); }
                finally { CpuLanes.Release(); }
                token.ThrowIfCancellationRequested();
                if (!Post(name, () => apply(result)))
                    owner.logger.Log(OperationsLogLevel.Warning, owner.logSource, $"CPU plugin result '{name}' could not be posted to the game thread.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { owner.executionGuard.Record($"cpu:{name}", ex); }
            finally
            {
                Interlocked.Decrement(ref worldCpuPending);
                Interlocked.Decrement(ref pluginCpuPending);
            }
        }

        private async Task RepeatAsync(string name, TimeSpan interval, Action action)
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(owner.lifetime.Token).ConfigureAwait(false))
                {
                    if (!Post(name, action))
                    {
                        owner.logger.Log(OperationsLogLevel.Warning, owner.logSource,
                            $"Scheduled plugin action '{name}' could not be posted to the game thread.");
                    }
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                owner.logger.Log(OperationsLogLevel.Error, owner.logSource,
                    $"Scheduled plugin action '{name}' failed.", ex);
            }
        }

        private async Task RunIoCoreAsync<T>(
            string name,
            Func<CancellationToken, ValueTask<T>> operation,
            Action<T> apply)
        {
            try
            {
                owner.lifetime.Token.ThrowIfCancellationRequested();
                T result = await operation(owner.lifetime.Token).ConfigureAwait(false);
                if (!Post(name, () => apply(result)))
                {
                    owner.logger.Log(OperationsLogLevel.Warning, owner.logSource,
                        $"Async plugin result '{name}' could not be posted to the game thread.");
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                owner.executionGuard.Record($"io:{name}", ex);
            }
        }

        private static void Validate(string name, Action action)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(action);
        }
    }
}
