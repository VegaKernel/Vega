namespace Vega.Terraria.Networking;

/// <summary>Explicit opt-in candidate limits; production bootstrap sizing still requires live-client measurements.</summary>
internal sealed record NativeTcpTransportOptions
{
    // Count ceiling is sized for 255 active peers; the cooperative 2ms time budget still applies.
    public const int DefaultCallbacksPerPass = 8192;
    public int MaxConnections { get; init; } = 512;
    public int MaxOutstandingPerConnection { get; init; } = 16_384;
    public long MaxRetainedBytesPerConnection { get; init; } = 32 * 1024 * 1024;
    public int MaxOutstandingPerWorld { get; init; } = 131_072;
    public long MaxRetainedBytesPerWorld { get; init; } = 256L * 1024 * 1024;
    public int MaxSendBytes { get; init; } = 1024 * 1024;
    public int MaxReceiveBytes { get; init; } = 128 * 1024;
    public int MaxCallbacksPerPass { get; init; } = DefaultCallbacksPerPass;
    public bool MeasureQueueAge { get; init; }
    public TimeSpan CallbackBudget { get; init; } = TimeSpan.FromMilliseconds(2);
    /// <summary>Bounded per-peer completion drain burst; a pass may finish at most this many callbacks after its last time check.</summary>
    public int CompletionDrainBatch { get; init; } = 1;
    /// <summary>Experimental adjacent-send coalescing, disabled until measured.</summary>
    public int MaxBatchRequests { get; init; } = 1;
    /// <summary>Experimental producer-owned contiguous blocks, avoiding writer-side merge copies.</summary>
    public bool ProducerBatching { get; init; }
    /// <summary>Use one wake for an idle writer instead of counting each queued request.</summary>
    public bool EdgeTriggeredWake { get; init; } = true;
    /// <summary>Experimental world-thread burst batching; flushes during and after native ticks.</summary>
    public bool DeferWriterWake { get; init; }
    public TimeSpan DeferredWakeFlushInterval { get; init; } = TimeSpan.FromMilliseconds(1);
    /// <summary>One cancellation registration wakes an idle writer; the per-wait mode remains for A/B measurement.</summary>
    public bool SingleCancellationWake { get; init; } = true;
    /// <summary>Avoid taking the outbound gate on empty completion probes; false is the A/B baseline.</summary>
    public bool SkipEmptyCompletionGate { get; init; } = true;
    /// <summary>Experimental A/B: close already disposes the socket to interrupt pending I/O.</summary>
    public bool CancelPendingSocketSend { get; init; } = true;

    public void Validate()
    {
        if (MaxConnections <= 0 || MaxOutstandingPerConnection <= 0 || MaxRetainedBytesPerConnection <= 0 ||
            MaxOutstandingPerWorld < MaxOutstandingPerConnection || MaxRetainedBytesPerWorld < MaxRetainedBytesPerConnection ||
            MaxSendBytes <= 0 || MaxSendBytes > MaxRetainedBytesPerConnection || MaxReceiveBytes <= 0 ||
            MaxCallbacksPerPass <= 0 || CallbackBudget <= TimeSpan.Zero || CompletionDrainBatch is < 1 or > 4 || MaxBatchRequests is < 1 or > NativeOutboundQueue.BatchRequestLimit ||
            (ProducerBatching && (!DeferWriterWake || MaxBatchRequests <= 1)) ||
            DeferredWakeFlushInterval <= TimeSpan.Zero || DeferredWakeFlushInterval > TimeSpan.FromMilliseconds(2))
            throw new ArgumentOutOfRangeException(nameof(NativeTcpTransportOptions), "Invalid native TCP transport limits.");
    }
}
