using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Networking;

public sealed record ServerNetworkOptions
{
    public int PendingConnectionLimit { get; init; } = 128;
    public int FramesPerSlotPerPass { get; init; } = 32;
    public bool UseOrderedTcp { get; init; } = true;
    public bool OrderedQueueDiagnostics { get; init; }
    /// <summary>Dense-burst writer batching; native ticks flush pending wakes.</summary>
    public bool OrderedDeferredWake { get; init; } = true;
    /// <summary>Producer-owned contiguous packet blocks, requires deferred Ordered batching.</summary>
    public bool OrderedProducerBatching { get; init; } = true;
    private const double DefaultWakeMilliseconds = 2;
    public double OrderedDeferredWakeMilliseconds { get; init; } = DefaultWakeMilliseconds;
    public int DistantMovementUpdatesPerSecond { get; init; } = 5;
    public int OrderedCallbacksPerPass { get; init; } = NativeTcpTransportOptions.DefaultCallbacksPerPass;
    public int OrderedCompletionDrainBatch { get; init; } = 4;
    public int OrderedBatchRequests { get; init; } = NativeOutboundQueue.BatchRequestLimit;

    public static ServerNetworkOptions Normalize(ServerNetworkOptions options) => options with
    {
        PendingConnectionLimit = Math.Clamp(options.PendingConnectionLimit, 1, 1024),
        FramesPerSlotPerPass = Math.Clamp(options.FramesPerSlotPerPass, 1, 256),
        DistantMovementUpdatesPerSecond = Math.Clamp(options.DistantMovementUpdatesPerSecond, 0, 20),
        OrderedDeferredWakeMilliseconds = double.IsFinite(options.OrderedDeferredWakeMilliseconds) ? Math.Clamp(options.OrderedDeferredWakeMilliseconds, 0.25, 2) : DefaultWakeMilliseconds,
        OrderedProducerBatching = options.OrderedProducerBatching && options.UseOrderedTcp && options.OrderedDeferredWake && options.OrderedBatchRequests > 1,
        OrderedCallbacksPerPass = Math.Clamp(options.OrderedCallbacksPerPass, 1, NativeTcpTransportOptions.DefaultCallbacksPerPass),
        OrderedCompletionDrainBatch = Math.Clamp(options.OrderedCompletionDrainBatch, 1, 4),
        OrderedBatchRequests = Math.Clamp(options.OrderedBatchRequests, 1, NativeOutboundQueue.BatchRequestLimit)
    };

    public static ServerNetworkOptions Load(string path, IOperationsLogger logger)
    {
        try
        {
            return File.Exists(path)
                ? Normalize(JsonSerializer.Deserialize<ServerNetworkOptions>(File.ReadAllText(path),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new())
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.Log(OperationsLogLevel.Warning, "network", "Invalid network configuration; using default budgets.", ex);
            return new();
        }
    }
}
