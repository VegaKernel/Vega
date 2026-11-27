using System.Text.Json;
using Vega.Contracts.Operations;

namespace Vega.Terraria.Networking;

public sealed record ServerNetworkOptions
{
    public int PendingConnectionLimit { get; init; } = 128;
    public int FramesPerSlotPerPass { get; init; } = 32;
    public bool UseOrderedTcp { get; init; }
    public bool OrderedQueueDiagnostics { get; init; }
    /// <summary>Opt-in dense-burst writer batching; native ticks flush pending wakes.</summary>
    public bool OrderedDeferredWake { get; init; }
    public double OrderedDeferredWakeMilliseconds { get; init; } = 1;
    public int DistantMovementUpdatesPerSecond { get; init; }
    public int OrderedCallbacksPerPass { get; init; } = NativeTcpTransportOptions.DefaultCallbacksPerPass;
    public int OrderedBatchRequests { get; init; } = NativeOutboundQueue.BatchRequestLimit;

    public static ServerNetworkOptions Normalize(ServerNetworkOptions options) => options with
    {
        PendingConnectionLimit = Math.Clamp(options.PendingConnectionLimit, 1, 1024),
        FramesPerSlotPerPass = Math.Clamp(options.FramesPerSlotPerPass, 1, 256),
        DistantMovementUpdatesPerSecond = Math.Clamp(options.DistantMovementUpdatesPerSecond, 0, 20),
        OrderedDeferredWakeMilliseconds = double.IsFinite(options.OrderedDeferredWakeMilliseconds) ? Math.Clamp(options.OrderedDeferredWakeMilliseconds, 0.25, 2) : 1,
        OrderedCallbacksPerPass = Math.Clamp(options.OrderedCallbacksPerPass, 1, NativeTcpTransportOptions.DefaultCallbacksPerPass),
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
