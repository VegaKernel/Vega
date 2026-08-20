using System.Diagnostics;
using HookWorldFile = HookEvents.Terraria.IO.WorldFile;
using Vega.Contracts.Operations;
using Vega.Contracts.Runtime;

namespace Vega.Terraria.Worlds;

/// <summary>
/// Owns the optional, disposable Vega world-image cache and stock-loader profiling hooks.
/// Terraria <c>.wld</c> files remain the only canonical save targets.
/// </summary>
public sealed class TerrariaWorldImageService : IDisposable, IWorldLoadMetricsReader
{
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

        RebuildAfterSave(sourcePath);
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

    private void ScheduleBuild(string sourcePath, string imagePath)
    {
        lock (buildSync)
        {
            if (disposed)
            {
                return;
            }

            pendingBuild = new BuildRequest(sourcePath, imagePath);
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

    internal void FlushPendingBuilds()
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

            worker.GetAwaiter().GetResult();
        }
    }

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
                    request.ImagePath);
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

    private sealed record BuildRequest(string SourcePath, string ImagePath);
}
