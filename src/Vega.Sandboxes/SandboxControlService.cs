using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vega.Contracts.Operations;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

public sealed class SandboxControlService : ISandboxControlService
{
    private readonly object sync = new();
    private readonly string baseDirectory;
    private readonly string executablePath;
    private readonly string instancesRoot;
    private readonly SandboxOptions options;
    private readonly SandboxCatalog catalog;
    private readonly SandboxMaterializer materializer;
    private readonly IOperationsLogger logger;
    private readonly byte[] authenticationKey;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim startGate;
    private readonly Dictionary<WorldId, RuntimeState> states;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task monitor;
    private bool disposed;

    public SandboxControlService(
        string baseDirectory,
        SandboxOptions options,
        IOperationsLogger logger,
        string? executablePath = null)
    {
        this.baseDirectory = Path.GetFullPath(baseDirectory);
        this.options = options;
        this.logger = logger;
        this.executablePath = Path.GetFullPath(executablePath ?? Environment.ProcessPath ??
            throw new InvalidOperationException("Current Vega executable path is unavailable."));
        string dataRoot = Path.Combine(this.baseDirectory, "data", "sandboxes");
        instancesRoot = Path.Combine(dataRoot, "instances");
        catalog = new SandboxCatalog(Path.Combine(dataRoot, "catalog.json"));
        materializer = new SandboxMaterializer(instancesRoot, options.MaximumInstanceMegabytes);
        authenticationKey = SandboxKeyFile.LoadOrCreate(Path.Combine(dataRoot, "control.key"));
        startGate = new SemaphoreSlim(options.MaximumConcurrentStarts, options.MaximumConcurrentStarts);
        states = catalog.Snapshot().ToDictionary(
            static descriptor => descriptor.WorldId,
            static descriptor => new RuntimeState(descriptor));
        ReconcileExistingProcesses();
        ReportOrphanedInstanceDirectories();
        monitor = MonitorAsync(stopping.Token);
    }

    public IReadOnlyList<SandboxStatus> Snapshot()
    {
        lock (sync)
        {
            return states.Values
                .OrderBy(static state => state.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(Snapshot)
                .ToArray();
        }
    }

    public async ValueTask<SandboxOperationResult> CreateAsync(
        SandboxCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!options.Enabled)
        {
            return Rejected("sandbox.disabled", "Sandbox management is disabled.");
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var worldId = new WorldId(request.WorldId.Trim());
            SandboxMaterializer.ValidateWorldId(worldId);
            if (states.ContainsKey(worldId))
            {
                RuntimeState existing = states[worldId];
                string requestedSource = Path.GetFullPath(request.SourceWorldPath);
                return string.Equals(existing.Descriptor.DisplayName, request.DisplayName.Trim(), StringComparison.Ordinal) &&
                       string.Equals(existing.Descriptor.SourceWorldPath, requestedSource, StringComparison.OrdinalIgnoreCase) &&
                       existing.Descriptor.Persistence == request.Persistence &&
                       existing.Descriptor.PlayerCapacity == request.PlayerCapacity
                    ? Already(existing, $"Sandbox '{worldId}' already exists with the requested descriptor.")
                    : Conflict("sandbox.exists", $"Sandbox '{worldId}' already exists with a different descriptor.");
            }

            ValidateCreate(request);
            string source = Path.GetFullPath(request.SourceWorldPath);
            string instance = request.Persistence == SandboxPersistenceMode.External
                ? source
                : materializer.InstancePath(worldId);
            int port = request.ListenerPort ?? NextPort();
            if (states.Values.Any(state => state.Descriptor.ListenerPort == port))
            {
                return Conflict("sandbox.port_in_use", $"Sandbox port {port} is already assigned.");
            }

            var descriptor = new SandboxDescriptor(
                worldId,
                request.DisplayName.Trim(),
                source,
                instance,
                request.Persistence,
                request.PlayerCapacity,
                request.Access with
                {
                    AllowedAccounts = request.Access.AllowedAccounts is null
                        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(request.Access.AllowedAccounts, StringComparer.OrdinalIgnoreCase)
                },
                request.IdleTimeToLive ?? (request.Persistence == SandboxPersistenceMode.Temporary
                    ? TimeSpan.FromMinutes(options.DefaultIdleTtlMinutes)
                    : null),
                request.SscMode,
                new HashSet<string>(request.EnabledModules, StringComparer.OrdinalIgnoreCase),
                port,
                1,
                DateTimeOffset.UtcNow,
                SandboxMaterializer.ComputeRevision(source));
            var state = new RuntimeState(descriptor);
            state.State = SandboxLifecycleState.Materializing;
            lock (sync) states.Add(worldId, state);
            try
            {
                if (descriptor.Persistence != SandboxPersistenceMode.External)
                {
                    await materializer.MaterializeAsync(worldId, source, cancellationToken).ConfigureAwait(false);
                }

                state.State = SandboxLifecycleState.Stopped;
                catalog.Add(descriptor);
                Log("created", state);
                return Applied(state, $"Sandbox '{worldId}' created.");
            }
            catch
            {
                lock (sync) states.Remove(worldId);
                throw;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.Log(OperationsLogLevel.Error, "sandboxes", "Sandbox creation failed.", ex);
            return Failed("sandbox.create_failed", ex.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask<SandboxOperationResult> StartAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(worldId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask<SandboxOperationResult> StopAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StopCoreAsync(worldId, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask<SandboxOperationResult> RestartAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SandboxOperationResult stopped = await StopCoreAsync(worldId, true, cancellationToken).ConfigureAwait(false);
            return stopped.Status == SandboxOperationStatus.NotFound
                ? stopped
                : await StartCoreAsync(worldId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask<SandboxOperationResult> ResetAsync(
        WorldId worldId,
        string? replacementSource = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryGet(worldId, out RuntimeState? state)) return NotFound(worldId);
            if (state.Descriptor.Persistence == SandboxPersistenceMode.External)
            {
                return Rejected("sandbox.external_reset", "Externally managed worlds cannot be reset by Vega.");
            }

            bool restart = state.DesiredRunning || state.Process is { HasExited: false };
            SandboxOperationResult stopped = await StopCoreAsync(worldId, restart, cancellationToken).ConfigureAwait(false);
            if (!stopped.IsSuccess) return stopped;
            state.State = SandboxLifecycleState.Resetting;
            string source = replacementSource is null
                ? state.Descriptor.SourceWorldPath
                : Path.GetFullPath(replacementSource);
            await materializer.ResetAsync(state.Descriptor, source, cancellationToken).ConfigureAwait(false);
            state.Descriptor = state.Descriptor with
            {
                SourceWorldPath = source,
                TemplateRevision = SandboxMaterializer.ComputeRevision(source),
                Revision = checked(state.Descriptor.Revision + 1)
            };
            catalog.Replace(state.Descriptor);
            state.State = SandboxLifecycleState.Stopped;
            Log("reset", state);
            return restart
                ? await StartCoreAsync(worldId, cancellationToken).ConfigureAwait(false)
                : Applied(state, $"Sandbox '{worldId}' reset.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.Log(OperationsLogLevel.Error, "sandboxes", $"Sandbox reset '{worldId}' failed.", ex);
            return Failed("sandbox.reset_failed", ex.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask<SandboxOperationResult> DestroyAsync(
        WorldId worldId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryGet(worldId, out RuntimeState? state)) return NotFound(worldId);
            SandboxOperationResult stopped = await StopCoreAsync(worldId, false, cancellationToken).ConfigureAwait(false);
            if (!stopped.IsSuccess) return stopped;
            state.State = SandboxLifecycleState.Destroying;
            materializer.Destroy(state.Descriptor);
            catalog.Remove(worldId);
            lock (sync) states.Remove(worldId);
            logger.Log(OperationsLogLevel.Warning, "sandboxes", $"Sandbox '{worldId}' destroyed.");
            return new SandboxOperationResult(SandboxOperationStatus.Applied, Message: $"Sandbox '{worldId}' destroyed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Log(OperationsLogLevel.Error, "sandboxes", $"Sandbox destroy '{worldId}' failed.", ex);
            return Failed("sandbox.destroy_failed", ex.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        WorldId[] running;
        lock (sync)
        {
            running = states.Values
                .Where(static state => state.Process is { HasExited: false })
                .Select(static state => state.Descriptor.WorldId)
                .ToArray();
        }

        foreach (WorldId worldId in running)
        {
            try
            {
                StopAsync(worldId).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Log(OperationsLogLevel.Error, "sandboxes", $"Sandbox '{worldId}' did not stop cleanly.", ex);
            }
        }

        stopping.Cancel();
        try { monitor.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        disposed = true;
        stopping.Dispose();
        operationGate.Dispose();
        startGate.Dispose();
    }

    private async ValueTask<SandboxOperationResult> StartCoreAsync(WorldId worldId, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return Rejected("sandbox.disabled", "Sandbox management is disabled.");
        if (!TryGet(worldId, out RuntimeState? state)) return NotFound(worldId);
        if (state.Descriptor.SscMode != SandboxSscMode.Disabled)
        {
            return Rejected(
                "sandbox.ssc_mode_unavailable",
                $"SSC mode '{state.Descriptor.SscMode}' is not available until the persistent SSC store ships; select Disabled explicitly.");
        }

        if (state.Process is { HasExited: false } && state.State == SandboxLifecycleState.Ready)
        {
            state.DesiredRunning = true;
            return Already(state, $"Sandbox '{worldId}' is already ready.");
        }

        int active;
        lock (sync) active = states.Values.Count(static candidate => candidate.Process is { HasExited: false });
        if (active >= options.MaximumActiveWorlds)
        {
            return Rejected("sandbox.admission_budget", "The global active-sandbox budget is full.");
        }

        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            state.State = SandboxLifecycleState.Starting;
            state.DesiredRunning = true;
            state.Failure = null;
            string worldPath = state.Descriptor.Persistence == SandboxPersistenceMode.External
                ? state.Descriptor.SourceWorldPath
                : await materializer.MaterializeAsync(worldId, state.Descriptor.SourceWorldPath, cancellationToken).ConfigureAwait(false);
            Guid instanceId = Guid.NewGuid();
            string statusPath = StatusPath(worldId);
            if (File.Exists(statusPath)) File.Delete(statusPath);
            Process process = Launch(state.Descriptor, worldPath, instanceId, statusPath);
            state.Process = process;
            state.InstanceId = instanceId;
            state.StartedAt = DateTimeOffset.UtcNow;
            state.ReadyAt = null;
            state.StatusPath = statusPath;
            Log("starting", state);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.StartTimeoutSeconds));
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException($"Backend exited with code {process.ExitCode} before readiness.");
                    }

                    if (TryRefreshHeartbeat(state) && state.State == SandboxLifecycleState.Ready)
                    {
                        Log("ready", state);
                        return Applied(state, $"Sandbox '{worldId}' is ready on port {state.Descriptor.ListenerPort}.");
                    }

                    await Task.Delay(250, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryTerminate(process);
                state.State = SandboxLifecycleState.Failed;
                state.Failure = "Readiness handshake timed out.";
                state.DesiredRunning = false;
                return new SandboxOperationResult(
                    SandboxOperationStatus.TimedOut,
                    Snapshot(state),
                    "sandbox.start_timeout",
                    state.Failure);
            }
            catch (OperationCanceledException)
            {
                TryTerminate(process);
                state.Process = null;
                state.State = SandboxLifecycleState.Stopped;
                state.DesiredRunning = false;
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                TryTerminate(process);
                state.State = SandboxLifecycleState.Failed;
                state.Failure = ex.Message;
                state.DesiredRunning = false;
                return Failed("sandbox.start_failed", ex.Message, state);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Failed("sandbox.start_failed", "Sandbox did not reach readiness.", state);
        }
        finally
        {
            startGate.Release();
        }
    }

    private async ValueTask<SandboxOperationResult> StopCoreAsync(
        WorldId worldId,
        bool preserveDesiredRunning,
        CancellationToken cancellationToken)
    {
        if (!TryGet(worldId, out RuntimeState? state)) return NotFound(worldId);
        bool desired = preserveDesiredRunning && state.DesiredRunning;
        state.DesiredRunning = desired;
        Process? process = state.Process;
        if (process is null || process.HasExited)
        {
            state.State = SandboxLifecycleState.Stopped;
            state.Process = null;
            return Already(state, $"Sandbox '{worldId}' is already stopped.");
        }

        state.State = SandboxLifecycleState.Stopping;
        Log("stopping", state);
        try
        {
            if (!process.StartInfo.RedirectStandardInput)
            {
                throw new IOException("The reconciled backend has no control stream.");
            }

            await process.StandardInput.WriteLineAsync("shutdown Sandbox control-plane stop").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.GracefulStopTimeoutSeconds));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
            logger.Log(OperationsLogLevel.Warning, "sandboxes", $"Forcing sandbox '{worldId}' to stop.", ex);
            TryTerminate(process);
        }

        process.Dispose();
        state.Process = null;
        state.State = SandboxLifecycleState.Stopped;
        state.LastHeartbeatAt = DateTimeOffset.UtcNow;
        Log("stopped", state);
        return Applied(state, $"Sandbox '{worldId}' stopped.");
    }

    private Process Launch(
        SandboxDescriptor descriptor,
        string worldPath,
        Guid instanceId,
        string statusPath)
    {
        bool managedDll = string.Equals(Path.GetExtension(executablePath), ".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managedDll ? "dotnet" : executablePath)
        {
            WorkingDirectory = baseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (managedDll) start.ArgumentList.Add(executablePath);
        start.ArgumentList.Add($"--vega-backend-world-id={descriptor.WorldId.Value}");
        start.ArgumentList.Add($"--vega-backend-instance={instanceId:D}");
        start.ArgumentList.Add($"--vega-backend-status={statusPath}");
        start.ArgumentList.Add($"--vega-backend-modules={string.Join(',', descriptor.EnabledModules)}");
        start.ArgumentList.Add("--vega-ui=headless");
        start.ArgumentList.Add("-world");
        start.ArgumentList.Add(worldPath);
        start.ArgumentList.Add("-port");
        start.ArgumentList.Add(descriptor.ListenerPort.ToString());
        start.ArgumentList.Add("-maxplayers");
        start.ArgumentList.Add(descriptor.PlayerCapacity.ToString());
        start.Environment["VEGA_BACKEND_TOKEN"] = Convert.ToBase64String(authenticationKey);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => LogBackend(descriptor.WorldId, args.Data, false);
        process.ErrorDataReceived += (_, args) => LogBackend(descriptor.WorldId, args.Data, true);
        if (!process.Start()) throw new InvalidOperationException("Sandbox backend process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) continue;
            List<(WorldId Id, bool Restart)> actions = [];
            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                lock (sync)
                {
                    foreach (RuntimeState state in states.Values)
                    {
                        if (state.Process is not { } process) continue;
                        if (process.HasExited)
                        {
                            process.Dispose();
                            state.Process = null;
                            bool restart = state.DesiredRunning && state.RestartCount < options.MaximumAutomaticRestarts;
                            state.State = restart ? SandboxLifecycleState.Degraded : SandboxLifecycleState.Failed;
                            state.Failure = restart ? "Backend exited; restart scheduled." : "Backend exited; restart budget exhausted.";
                            if (restart)
                            {
                                state.RestartCount++;
                                state.DesiredRunning = false;
                                actions.Add((state.Descriptor.WorldId, true));
                            }

                            continue;
                        }

                        TryRefreshHeartbeat(state);
                        SampleCpu(state, process, now);
                        if (state.LastHeartbeatAt is DateTimeOffset heartbeat &&
                            now - heartbeat > TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds))
                        {
                            state.State = SandboxLifecycleState.Degraded;
                            state.Failure = "Backend heartbeat is stale.";
                        }

                        if (state.CpuPercent > options.MaximumCpuPercent)
                        {
                            state.CpuBudgetBreaches++;
                        }
                        else
                        {
                            state.CpuBudgetBreaches = 0;
                        }

                        if (state.CpuBudgetBreaches >= 3)
                        {
                            state.Failure = "Backend exceeded its sustained CPU budget.";
                            state.DesiredRunning = false;
                            actions.Add((state.Descriptor.WorldId, false));
                        }
                        else if (state.WorkingSetBytes > checked((long)options.MaximumWorkingSetMegabytes * 1024 * 1024))
                        {
                            state.Failure = "Backend exceeded its working-set budget.";
                            state.DesiredRunning = false;
                            actions.Add((state.Descriptor.WorldId, false));
                        }
                        else if (state.Descriptor.IdleTimeToLive is TimeSpan ttl && state.OnlinePlayers == 0 &&
                                 state.ReadyAt is DateTimeOffset readyAt && now - readyAt >= ttl)
                        {
                            state.DesiredRunning = false;
                            actions.Add((state.Descriptor.WorldId, false));
                        }
                    }
                }
            }
            finally
            {
                operationGate.Release();
            }

            foreach ((WorldId id, bool restart) in actions.Distinct())
            {
                if (restart)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.RestartBackoffSeconds), cancellationToken).ConfigureAwait(false);
                    await StartAsync(id, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await StopAsync(id, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private void ReconcileExistingProcesses()
    {
        foreach (RuntimeState state in states.Values)
        {
            state.State = SandboxLifecycleState.Stopped;
            string statusPath = StatusPath(state.Descriptor.WorldId);
            if (!File.Exists(statusPath) ||
                !SandboxHeartbeatCodec.TryRead(statusPath, authenticationKey, out SandboxBackendHeartbeat? heartbeat) ||
                heartbeat is null || heartbeat.WorldId != state.Descriptor.WorldId ||
                !string.Equals(heartbeat.RuntimeVersion, RuntimeVersion, StringComparison.Ordinal) ||
                DateTimeOffset.UtcNow - heartbeat.Timestamp > TimeSpan.FromSeconds(options.HeartbeatTimeoutSeconds))
            {
                continue;
            }

            try
            {
                Process process = Process.GetProcessById(heartbeat.ProcessId);
                if (process.HasExited) continue;
                state.Process = process;
                state.InstanceId = heartbeat.InstanceId;
                state.StatusPath = statusPath;
                state.DesiredRunning = true;
                ApplyHeartbeat(state, heartbeat);
                logger.Log(OperationsLogLevel.Information, "sandboxes", $"Reconciled running sandbox '{state.Descriptor.WorldId}' (PID {heartbeat.ProcessId}).");
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private void ReportOrphanedInstanceDirectories()
    {
        if (!Directory.Exists(instancesRoot)) return;
        HashSet<string> known = states.Keys.Select(static id => id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in Directory.EnumerateDirectories(instancesRoot))
        {
            string name = Path.GetFileName(directory);
            if (!known.Contains(name))
            {
                logger.Log(
                    OperationsLogLevel.Warning,
                    "sandboxes",
                    $"Orphaned sandbox instance directory detected and preserved: '{directory}'.");
            }
        }
    }

    private bool TryRefreshHeartbeat(RuntimeState state)
    {
        if (state.StatusPath is null || !File.Exists(state.StatusPath) ||
            !SandboxHeartbeatCodec.TryRead(state.StatusPath, authenticationKey, out SandboxBackendHeartbeat? heartbeat) ||
            heartbeat is null || heartbeat.WorldId != state.Descriptor.WorldId ||
            !string.Equals(heartbeat.RuntimeVersion, RuntimeVersion, StringComparison.Ordinal) ||
            heartbeat.InstanceId != state.InstanceId || heartbeat.ProcessId != state.Process?.Id)
        {
            return false;
        }

        ApplyHeartbeat(state, heartbeat);
        return true;
    }

    private static void ApplyHeartbeat(RuntimeState state, SandboxBackendHeartbeat heartbeat)
    {
        state.State = heartbeat.State;
        state.LastHeartbeatAt = heartbeat.Timestamp;
        state.StartedAt = heartbeat.StartedAt;
        state.OnlinePlayers = heartbeat.OnlinePlayers;
        state.TicksPerSecond = heartbeat.TicksPerSecond;
        state.WorkingSetBytes = heartbeat.WorkingSetBytes;
        state.ReadyAt ??= heartbeat.State == SandboxLifecycleState.Ready ? heartbeat.Timestamp : null;
        if (heartbeat.State == SandboxLifecycleState.Ready) state.Failure = null;
    }

    private static void SampleCpu(RuntimeState state, Process process, DateTimeOffset now)
    {
        TimeSpan cpu;
        try
        {
            cpu = process.TotalProcessorTime;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        if (state.LastCpuSampleAt is DateTimeOffset previousAt)
        {
            double elapsed = (now - previousAt).TotalSeconds;
            if (elapsed > 0)
            {
                state.CpuPercent = Math.Max(0,
                    (cpu - state.LastCpuTime).TotalSeconds / elapsed / Environment.ProcessorCount * 100);
            }
        }

        state.LastCpuSampleAt = now;
        state.LastCpuTime = cpu;
    }

    private int NextPort()
    {
        HashSet<int> occupied = states.Values.Select(static state => state.Descriptor.ListenerPort).ToHashSet();
        for (int port = options.BasePort; port <= 65535; port++)
        {
            if (!occupied.Contains(port)) return port;
        }

        throw new InvalidOperationException("No sandbox listener port remains in the configured range.");
    }

    private static void ValidateCreate(SandboxCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 128 ||
            request.PlayerCapacity is < 1 or > 255 ||
            request.ListenerPort is < 1 or > 65535 ||
            request.IdleTimeToLive is { } ttl && (ttl < TimeSpan.FromMinutes(1) || ttl > TimeSpan.FromDays(30)) ||
            request.Access.Mode == SandboxAccessMode.Permission && string.IsNullOrWhiteSpace(request.Access.Permission))
        {
            throw new ArgumentException("Sandbox descriptor contains an invalid display name, capacity, port, TTL or access policy.");
        }

        _ = SandboxMaterializer.ComputeRevision(Path.GetFullPath(request.SourceWorldPath));
    }

    private bool TryGet(WorldId worldId, [NotNullWhen(true)] out RuntimeState? state)
    {
        lock (sync) return states.TryGetValue(worldId, out state);
    }

    private string StatusPath(WorldId worldId) => Path.Combine(instancesRoot, worldId.Value, "backend-status.json");

    private static string RuntimeVersion =>
        typeof(SandboxControlService).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static SandboxStatus Snapshot(RuntimeState state) => new(
        state.Descriptor,
        state.State,
        state.State == SandboxLifecycleState.Ready ? new SandboxEndpoint("127.0.0.1", state.Descriptor.ListenerPort) : null,
        state.Process is { HasExited: false } process ? process.Id : null,
        state.StartedAt,
        state.LastHeartbeatAt,
        state.ReadyAt,
        state.OnlinePlayers,
        state.TicksPerSecond,
        new SandboxResourceSnapshot(state.CpuPercent, state.WorkingSetBytes, File.Exists(state.Descriptor.InstanceWorldPath)
            ? new FileInfo(state.Descriptor.InstanceWorldPath).Length
            : 0),
        state.RestartCount,
        state.Failure,
        state.DesiredRunning);

    private static SandboxOperationResult Applied(RuntimeState state, string message) =>
        new(SandboxOperationStatus.Applied, Snapshot(state), Message: message);

    private static SandboxOperationResult Already(RuntimeState state, string message) =>
        new(SandboxOperationStatus.AlreadyInDesiredState, Snapshot(state), Message: message);

    private static SandboxOperationResult NotFound(WorldId worldId) =>
        new(SandboxOperationStatus.NotFound, ErrorCode: "sandbox.not_found", Message: $"Sandbox '{worldId}' was not found.");

    private static SandboxOperationResult Rejected(string code, string message) =>
        new(SandboxOperationStatus.Rejected, ErrorCode: code, Message: message);

    private static SandboxOperationResult Conflict(string code, string message) =>
        new(SandboxOperationStatus.Conflict, ErrorCode: code, Message: message);

    private static SandboxOperationResult Failed(string code, string message, RuntimeState? state = null) =>
        new(SandboxOperationStatus.Failed, state is null ? null : Snapshot(state), code, message);

    private void Log(string transition, RuntimeState state) => logger.Log(
        OperationsLogLevel.Information,
        "sandboxes",
        $"Sandbox '{state.Descriptor.WorldId}' transition: {transition}; state={state.State}; revision={state.Descriptor.Revision}.");

    private void LogBackend(WorldId worldId, string? text, bool error)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            logger.Log(error ? OperationsLogLevel.Warning : OperationsLogLevel.Debug, $"sandbox:{worldId}", text);
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(5_000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class RuntimeState(SandboxDescriptor descriptor)
    {
        public SandboxDescriptor Descriptor { get; set; } = descriptor;
        public SandboxLifecycleState State { get; set; } = SandboxLifecycleState.Defined;
        public Process? Process { get; set; }
        public Guid InstanceId { get; set; }
        public string? StatusPath { get; set; }
        public bool DesiredRunning { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? LastHeartbeatAt { get; set; }
        public DateTimeOffset? ReadyAt { get; set; }
        public int OnlinePlayers { get; set; }
        public double TicksPerSecond { get; set; }
        public long WorkingSetBytes { get; set; }
        public double CpuPercent { get; set; }
        public DateTimeOffset? LastCpuSampleAt { get; set; }
        public TimeSpan LastCpuTime { get; set; }
        public int CpuBudgetBreaches { get; set; }
        public int RestartCount { get; set; }
        public string? Failure { get; set; }
    }
}
