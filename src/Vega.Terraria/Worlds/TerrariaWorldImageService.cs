using System.Diagnostics;
using HookWorldFile = HookEvents.Terraria.IO.WorldFile;
using HookWorldGen = HookEvents.Terraria.WorldGen;
using Vega.Contracts.Operations;
using Vega.Contracts.Runtime;

namespace Vega.Terraria.Worlds;

/// <summary>
/// Owns the optional, disposable Vega world-image cache and stock-loader profiling hooks.
/// Terraria <c>.wld</c> files remain the only canonical save targets.
/// </summary>
public sealed class TerrariaWorldImageService : IDisposable, IWorldLoadMetricsReader
{
    // The Terraria 1.4.5.7 integration baseline stopped generating Liquid hooks after Liquid became a value type.
    // Keep the cache correct by using Terraria's stock liquid initialization until upstream exposes an equivalent hook.
    private const bool SupportsLiquidRuntimeFastRestore = false;

    private readonly object sync = new();
    private readonly object buildSync = new();
    private readonly IOperationsLogger logger;
    private readonly IRuntimeLifecycle lifecycle;
    private readonly bool enabled;
    private WorldLoadMetricsSnapshot snapshot = EmptySnapshot();
    private ActiveImage? activeImage;
    private BuildRequest? pendingBuild;
    private Task? buildWorker;
    private bool disposed;
    private bool attached;
    private long loadStarted;
    private long versionLoadStarted;
    private long versionLoadEnded;
    private long tileDecodeTicks;
    private long chestTicks;
    private long signTicks;
    private long npcTicks;
    private long tileEntityTicks;
    private long allocatedBefore;
    private int gen0Before;
    private int gen1Before;
    private int gen2Before;
    private int waterCheckCount;

    public TerrariaWorldImageService(
        IOperationsLogger logger,
        IRuntimeLifecycle lifecycle,
        bool enabled)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        this.enabled = enabled;
    }

    public void Attach()
    {
        if (attached)
        {
            return;
        }

        HookWorldFile.LoadWorld += OnLoadWorld;
        HookWorldFile.LoadWorld_Version2 += OnLoadWorldVersion2;
        HookWorldFile.LoadWorldTiles += OnLoadWorldTiles;
        HookWorldFile.LoadChests += OnLoadChests;
        HookWorldFile.LoadSigns += OnLoadSigns;
        HookWorldFile.LoadNPCs += OnLoadNpcs;
        HookWorldFile.LoadTileEntities += OnLoadTileEntities;
        HookWorldFile.InternalSaveWorld += OnInternalSaveWorld;
        HookWorldGen.WaterCheck += OnWaterCheck;
        global::HookEvents.Terraria.Main.get_worldPathName += OnGetWorldPathName;
        lifecycle.Transitioned += OnLifecycleTransition;
        attached = true;
    }

    public WorldLoadMetricsSnapshot Read()
    {
        lock (sync)
        {
            return snapshot;
        }
    }

    public void Dispose()
    {
        if (attached)
        {
            HookWorldFile.LoadWorld -= OnLoadWorld;
            HookWorldFile.LoadWorld_Version2 -= OnLoadWorldVersion2;
            HookWorldFile.LoadWorldTiles -= OnLoadWorldTiles;
            HookWorldFile.LoadChests -= OnLoadChests;
            HookWorldFile.LoadSigns -= OnLoadSigns;
            HookWorldFile.LoadNPCs -= OnLoadNpcs;
            HookWorldFile.LoadTileEntities -= OnLoadTileEntities;
            HookWorldFile.InternalSaveWorld -= OnInternalSaveWorld;
            HookWorldGen.WaterCheck -= OnWaterCheck;
            global::HookEvents.Terraria.Main.get_worldPathName -= OnGetWorldPathName;
            lifecycle.Transitioned -= OnLifecycleTransition;
            attached = false;
        }

        Task? worker;
        lock (buildSync)
        {
            disposed = true;
            worker = buildWorker;
        }

        worker?.GetAwaiter().GetResult();
    }

    private void OnLoadWorld(object? sender, HookWorldFile.LoadWorldEventArgs args)
    {
        args.ContinueExecution = false;
        string? sourcePath = global::Terraria.Main.ActiveWorldFileData?.Path;
        bool localSource = global::Terraria.Main.ActiveWorldFileData is { IsCloudSave: false } &&
            !string.IsNullOrWhiteSpace(sourcePath) &&
            File.Exists(sourcePath);
        string cacheReason = localSource ? "absent" : "unsupported-source";
        bool cacheHit = false;
        bool runtimeSnapshotHit = false;
        long sourceBytes = localSource ? new FileInfo(sourcePath!).Length : 0;
        long imageBytes = 0;
        string? imagePath = localSource ? Path.ChangeExtension(sourcePath, ".vworld") : null;

        if (!enabled)
        {
            cacheReason = "disabled-by-default";
        }
        else if (localSource)
        {
            WorldImageValidationResult validation = VegaWorldImageReader.Validate(sourcePath!, imagePath!);
            cacheReason = validation.Reason;
            if (validation is { IsValid: true, Manifest: not null })
            {
                activeImage = new ActiveImage(imagePath!, validation.Manifest);
                imageBytes = new FileInfo(imagePath!).Length;
                cacheHit = true;
                runtimeSnapshotHit = SupportsLiquidRuntimeFastRestore &&
                    validation.Manifest.SnapshotFlags.HasFlag(
                        WorldImageSnapshotFlags.LiquidRuntimeState);
            }
            else if (IsCorruptionReason(validation.Reason))
            {
                Quarantine(imagePath!, validation.Reason);
            }
        }

        ResetMeasurements();
        loadStarted = Stopwatch.GetTimestamp();
        allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        gen0Before = GC.CollectionCount(0);
        gen1Before = GC.CollectionCount(1);
        gen2Before = GC.CollectionCount(2);

        bool fellBack = false;
        try
        {
            args.OriginalMethod();
            EnsureRuntimeLiquidStateWasRestored();
        }
        catch (Exception ex) when (cacheHit)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "world-image",
                "world_image cache=fallback reason=cache-load-exception; retrying canonical .wld",
                ex);
            activeImage = null;
            cacheHit = false;
            runtimeSnapshotHit = false;
            cacheReason = "cache-load-exception";
            fellBack = true;
            Quarantine(imagePath!, cacheReason);
            ResetMeasurements();
            loadStarted = Stopwatch.GetTimestamp();
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            gen0Before = GC.CollectionCount(0);
            gen1Before = GC.CollectionCount(1);
            gen2Before = GC.CollectionCount(2);
            args.OriginalMethod();
        }

        if (cacheHit && global::Terraria.WorldGen.loadFailed)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "world-image",
                "world_image cache=fallback reason=cache-load-failed; retrying canonical .wld");
            activeImage = null;
            cacheHit = false;
            runtimeSnapshotHit = false;
            cacheReason = "cache-load-failed";
            fellBack = true;
            ResetMeasurements();
            loadStarted = Stopwatch.GetTimestamp();
            allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            gen0Before = GC.CollectionCount(0);
            gen1Before = GC.CollectionCount(1);
            gen2Before = GC.CollectionCount(2);
            args.OriginalMethod();
        }

        long loadEnded = Stopwatch.GetTimestamp();
        activeImage = null;
        TimeSpan total = Stopwatch.GetElapsedTime(loadStarted, loadEnded);
        TimeSpan fileRead = versionLoadStarted == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(loadStarted, versionLoadStarted);
        TimeSpan postLoad = versionLoadEnded == 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(versionLoadEnded, loadEnded);
        var next = new WorldLoadMetricsSnapshot(
            cacheHit,
            cacheReason,
            sourceBytes,
            imageBytes,
            fileRead,
            StopwatchTicks(tileDecodeTicks),
            StopwatchTicks(chestTicks),
            StopwatchTicks(signTicks),
            StopwatchTicks(npcTicks),
            StopwatchTicks(tileEntityTicks),
            postLoad,
            cacheHit ? TimeSpan.Zero : total,
            cacheHit ? total : TimeSpan.Zero,
            Read().WorldImageBuildDuration,
            Read().WorldReadyDuration,
            GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore,
            GC.CollectionCount(0) - gen0Before,
            GC.CollectionCount(1) - gen1Before,
            GC.CollectionCount(2) - gen2Before,
            DateTimeOffset.UtcNow);
        lock (sync)
        {
            snapshot = next;
        }

        logger.Log(
            OperationsLogLevel.Information,
            "world-image",
            $"world_image cache={(cacheHit ? "hit" : "miss")} reason={cacheReason} " +
            $"runtime_snapshot={runtimeSnapshotHit.ToString().ToLowerInvariant()} " +
            $"fallback={fellBack.ToString().ToLowerInvariant()} source_bytes={sourceBytes} image_bytes={imageBytes} " +
            $"file_read_ms={fileRead.TotalMilliseconds:F1} tile_decode_ms={next.TileDecodeDuration.TotalMilliseconds:F1} " +
            $"chests_ms={next.ChestCreationDuration.TotalMilliseconds:F1} signs_ms={next.SignCreationDuration.TotalMilliseconds:F1} " +
            $"npcs_ms={next.NpcCreationDuration.TotalMilliseconds:F1} entities_ms={next.TileEntityCreationDuration.TotalMilliseconds:F1} " +
            $"post_load_ms={postLoad.TotalMilliseconds:F1} " +
            $"total_ms={total.TotalMilliseconds:F1} allocated_bytes={next.AllocatedBytes} " +
            $"gc0={next.Gen0Collections} gc1={next.Gen1Collections} gc2={next.Gen2Collections}");

        if (enabled && localSource && !cacheHit && !global::Terraria.WorldGen.loadFailed)
        {
            ScheduleBuild(sourcePath!, imagePath!);
        }
    }

    private void OnLoadWorldVersion2(object? sender, HookWorldFile.LoadWorld_Version2EventArgs args)
    {
        args.ContinueExecution = false;
        versionLoadStarted = Stopwatch.GetTimestamp();
        args.HookReturnValue = args.OriginalMethod(args.reader);
        versionLoadEnded = Stopwatch.GetTimestamp();
    }

    private void OnLoadWorldTiles(object? sender, HookWorldFile.LoadWorldTilesEventArgs args)
    {
        args.ContinueExecution = false;
        long started = Stopwatch.GetTimestamp();
        if (activeImage is { } image)
        {
            WorldImageTileLoader.Load(image.Path, image.Manifest);
            args.reader.BaseStream.Position = image.Manifest.SourceTileEnd;
        }
        else
        {
            args.OriginalMethod(args.reader, args.importance);
        }

        tileDecodeTicks += Stopwatch.GetTimestamp() - started;
    }

    private void OnLoadChests(object? sender, HookWorldFile.LoadChestsEventArgs args)
    {
        args.ContinueExecution = false;
        chestTicks += Measure(() => args.OriginalMethod(args.reader));
    }

    private void OnLoadSigns(object? sender, HookWorldFile.LoadSignsEventArgs args)
    {
        args.ContinueExecution = false;
        signTicks += Measure(() => args.OriginalMethod(args.reader));
    }

    private void OnLoadNpcs(object? sender, HookWorldFile.LoadNPCsEventArgs args)
    {
        args.ContinueExecution = false;
        npcTicks += Measure(() => args.OriginalMethod(args.reader));
    }

    private void OnLoadTileEntities(object? sender, HookWorldFile.LoadTileEntitiesEventArgs args)
    {
        args.ContinueExecution = false;
        tileEntityTicks += Measure(() => args.OriginalMethod(args.reader));
    }

    private void OnInternalSaveWorld(
        object? sender,
        HookWorldFile.InternalSaveWorldEventArgs args)
    {
        args.ContinueExecution = false;
        args.OriginalMethod(args.useCloudSaving, args.resetTime, args.useTemps);

        if (!enabled || args.useCloudSaving)
        {
            return;
        }

        string? sourcePath = global::Terraria.Main.ActiveWorldFileData?.Path;
        if (global::Terraria.Main.ActiveWorldFileData is not { IsCloudSave: false } ||
            string.IsNullOrWhiteSpace(sourcePath) ||
            !File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            long started = Stopwatch.GetTimestamp();
            WorldImageRuntimeSnapshot runtimeSnapshot = WorldImageRuntimeSnapshot.Capture(sourcePath);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            logger.Log(
                OperationsLogLevel.Information,
                "world-image",
                $"world_image snapshot=captured tile_bytes={runtimeSnapshot.TileRuns.Length} " +
                $"liquid_bytes={runtimeSnapshot.LiquidState.Length} duration_ms={elapsed.TotalMilliseconds:F1}");
            ScheduleBuild(
                sourcePath,
                Path.ChangeExtension(sourcePath, ".vworld"),
                runtimeSnapshot);
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "world-image",
                "world_image snapshot=failed; rebuilding a file-derived cache with stock liquid initialization",
                ex);
            RebuildAfterSave(sourcePath);
        }
    }

    private void OnWaterCheck(object? sender, HookWorldGen.WaterCheckEventArgs args)
    {
        if (!CanRestoreRuntimeLiquidState())
        {
            return;
        }

        args.ContinueExecution = false;
        waterCheckCount++;
        if (waterCheckCount == 1)
        {
            global::Terraria.Liquid.numLiquid = 0;
            global::Terraria.LiquidBuffer.numLiquidBuffer = 0;
            return;
        }

        if (waterCheckCount == 2)
        {
            ActiveImage image = activeImage!;
            WorldImageLiquidStateLoader.Load(image.Path, image.Manifest);
        }
    }

    private void OnGetWorldPathName(
        object? sender,
        global::HookEvents.Terraria.Main.get_worldPathNameEventArgs args)
    {
        if (activeImage is null)
        {
            return;
        }

        args.HookReturnValue = activeImage.Path;
        args.ContinueExecution = false;
    }

    private void OnLifecycleTransition(RuntimeLifecycleTransition transition)
    {
        if (transition.Stage != RuntimeLifecycleStage.WorldReady)
        {
            return;
        }

        lock (sync)
        {
            snapshot = snapshot with
            {
                WorldReadyDuration = transition.StageDuration,
                SampledAt = DateTimeOffset.UtcNow
            };
        }
    }

    private void ScheduleBuild(
        string sourcePath,
        string imagePath,
        WorldImageRuntimeSnapshot? runtimeSnapshot = null)
    {
        lock (buildSync)
        {
            if (disposed)
            {
                return;
            }

            pendingBuild = new BuildRequest(sourcePath, imagePath, runtimeSnapshot);
            buildWorker ??= Task.Run(ProcessBuildQueue);
        }
    }

    internal void RebuildAfterSave(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (enabled)
        {
            ScheduleBuild(sourcePath, Path.ChangeExtension(sourcePath, ".vworld"));
        }
    }

    public async ValueTask FlushPendingBuildsAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? worker;
            lock (buildSync)
            {
                worker = buildWorker;
            }

            if (worker is null)
            {
                return;
            }

            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void FlushPendingBuilds() => FlushPendingBuildsAsync().AsTask().GetAwaiter().GetResult();

    private void ProcessBuildQueue()
    {
        while (true)
        {
            BuildRequest? request;
            lock (buildSync)
            {
                request = pendingBuild;
                pendingBuild = null;
                if (request is null)
                {
                    buildWorker = null;
                    return;
                }
            }

            try
            {
                WorldImageBuildResult result = VegaWorldImageCompiler.Build(
                    request.SourcePath,
                    request.ImagePath,
                    request.RuntimeSnapshot);
                lock (sync)
                {
                    snapshot = snapshot with
                    {
                        ImageBytes = result.ImageBytes,
                        WorldImageBuildDuration = result.Duration,
                        SampledAt = DateTimeOffset.UtcNow
                    };
                }

                logger.Log(
                    OperationsLogLevel.Information,
                    "world-image",
                    $"world_image build=complete source_bytes={result.SourceBytes} image_bytes={result.ImageBytes} " +
                    $"duration_ms={result.Duration.TotalMilliseconds:F1}");
            }
            catch (Exception ex)
            {
                logger.Log(
                    OperationsLogLevel.Warning,
                    "world-image",
                    "world_image build=failed; canonical .wld remains authoritative",
                    ex);
            }
        }
    }

    private void Quarantine(string imagePath, string reason)
    {
        try
        {
            string quarantinePath = imagePath + ".corrupt";
            File.Move(imagePath, quarantinePath, overwrite: true);
            logger.Log(
                OperationsLogLevel.Warning,
                "world-image",
                $"world_image cache=quarantined reason={reason} path={quarantinePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "world-image",
                $"world_image cache=quarantine-failed reason={reason}",
                ex);
        }
    }

    private void ResetMeasurements()
    {
        versionLoadStarted = 0;
        versionLoadEnded = 0;
        tileDecodeTicks = 0;
        chestTicks = 0;
        signTicks = 0;
        npcTicks = 0;
        tileEntityTicks = 0;
        waterCheckCount = 0;
    }

    private bool CanRestoreRuntimeLiquidState() =>
        SupportsLiquidRuntimeFastRestore &&
        activeImage?.Manifest.SnapshotFlags.HasFlag(
            WorldImageSnapshotFlags.LiquidRuntimeState) == true;

    private void EnsureRuntimeLiquidStateWasRestored()
    {
        if (CanRestoreRuntimeLiquidState() && waterCheckCount != 2)
        {
            throw new InvalidDataException(
                $"Terraria liquid initialization sequence changed; expected two WaterCheck calls, observed {waterCheckCount}.");
        }
    }

    private static long Measure(Action action)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetTimestamp() - started;
    }

    private static TimeSpan StopwatchTicks(long ticks) =>
        TimeSpan.FromSeconds((double)ticks / Stopwatch.Frequency);

    private static bool IsCorruptionReason(string reason) =>
        reason is "truncated-footer" or "bad-footer-magic" or "bad-manifest-range" or
            "manifest-checksum" or "section-range" or "source-section-checksum" or
            "tile-section-checksum" or "truncated" or "invalid-format";

    private static WorldLoadMetricsSnapshot EmptySnapshot() =>
        new(
            false,
            "not-loaded",
            0,
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            0,
            0,
            0,
            0,
            DateTimeOffset.UtcNow);

    private sealed record ActiveImage(string Path, VegaWorldImageManifest Manifest);

    private sealed record BuildRequest(
        string SourcePath,
        string ImagePath,
        WorldImageRuntimeSnapshot? RuntimeSnapshot);
}
