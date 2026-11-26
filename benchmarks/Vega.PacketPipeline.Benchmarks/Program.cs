using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Multiplicity.Packets;
using Multiplicity.Packets.Models;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Core.Networking;
using Vega.Terraria.Networking;

const int sessionCount = 200;
const int packetsPerTick = 256;
const int measuredTicks = 4_000;
const int warmupPackets = 50_000;

byte[][] payloads = Enumerable.Range(0, sessionCount)
    .Select(static slot => Serialize(new PlayerUpdate { PlayerId = (byte)slot }))
    .ToArray();
bool realisticReceiveBuffer = args.Contains("--receive-buffer", StringComparer.Ordinal);
const int receiveBufferSize = 128 * 1024;
int payloadOffset = realisticReceiveBuffer ? 128 : 0;
byte[][] buffers = realisticReceiveBuffer
    ? payloads.Select(payload =>
    {
        var receiveBuffer = new byte[receiveBufferSize];
        payload.CopyTo(receiveBuffer, payloadOffset);
        return receiveBuffer;
    }).ToArray()
    : payloads;
var processor = new PlayerPacketOwnershipPolicy();
bool measureMetadata = args.Contains("--metadata", StringComparer.Ordinal);
var metadataRegistry = new TerrariaConnectionMetadataRegistry();

for (int index = 0; index < warmupPackets; index++)
{
    ProcessPacket(index);
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

long allocatedBefore = GC.GetTotalAllocatedBytes(true);
int[] collectionsBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
long? contextSwitchesBefore = ReadLinuxContextSwitches();
using System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess();
TimeSpan cpuBefore = process.TotalProcessorTime;
long started = Stopwatch.GetTimestamp();
var tickNanoseconds = new double[measuredTicks];

for (int tick = 0; tick < measuredTicks; tick++)
{
    long tickStarted = Stopwatch.GetTimestamp();
    int firstPacket = tick * packetsPerTick;
    for (int packet = 0; packet < packetsPerTick; packet++)
    {
        ProcessPacket(firstPacket + packet);
    }

    tickNanoseconds[tick] = Stopwatch.GetElapsedTime(tickStarted).TotalNanoseconds;
}

TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
process.Refresh();
TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
long? contextSwitchesAfter = ReadLinuxContextSwitches();
long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
int packetCount = packetsPerTick * measuredTicks;
Array.Sort(tickNanoseconds);

Console.WriteLine(JsonSerializer.Serialize(new
{
    Runtime = Environment.Version.ToString(),
    Framework = AppContext.TargetFrameworkName,
    OperatingSystem = Environment.OSVersion.ToString(),
    ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    Variant = measureMetadata ? "synchronous-bounded-memory-player-update-ownership-and-metadata" : "synchronous-bounded-memory-player-update-ownership",
    ReceiveBufferBytes = buffers[0].Length,
    PayloadWindowBytes = payloads[0].Length,
    Sessions = sessionCount,
    PacketCount = packetCount,
    PacketsPerTick = packetsPerTick,
    ElapsedMilliseconds = elapsed.TotalMilliseconds,
    PacketsPerSecond = packetCount / elapsed.TotalSeconds,
    NanosecondsPerPacket = elapsed.TotalNanoseconds / packetCount,
    AllocatedBytes = allocated,
    AllocatedBytesPerPacket = (double)allocated / packetCount,
    CpuMilliseconds = cpu.TotalMilliseconds,
    CpuNanosecondsPerPacket = cpu.TotalNanoseconds / packetCount,
    Gen0Collections = GC.CollectionCount(0) - collectionsBefore[0],
    Gen1Collections = GC.CollectionCount(1) - collectionsBefore[1],
    Gen2Collections = GC.CollectionCount(2) - collectionsBefore[2],
    ContextSwitches = contextSwitchesBefore.HasValue && contextSwitchesAfter.HasValue
        ? contextSwitchesAfter - contextSwitchesBefore
        : null,
    ContextSwitchSource = contextSwitchesBefore.HasValue ? "/proc/self/status" : "unavailable-on-host",
    TickLatencyNanoseconds = new
    {
        P50 = Percentile(0.50),
        P95 = Percentile(0.95),
        P99 = Percentile(0.99),
        Max = tickNanoseconds[^1]
    }
}, new JsonSerializerOptions { WriteIndented = true }));

void ProcessPacket(int sequence)
{
    int slot = sequence % sessionCount;
    byte[] payload = payloads[slot];
    if (measureMetadata && metadataRegistry.ObserveInboundDetailed((byte)PacketTypes.PlayerUpdate,
        buffers[slot].AsSpan(payloadOffset, payload.Length), slot, TerrariaConnectionState.FullyConnected, null).Decision.Kind != PacketDecisionKind.Allow)
        throw new InvalidOperationException("Ordinary packet metadata was unexpectedly rejected.");
    PacketProcessingDecision decision = MultiplicityPacketProcessing.ProcessInboundPayload(
        (byte)PacketTypes.PlayerUpdate,
        buffers[slot],
        payloadOffset,
        payload.Length,
        new ConnectionId(slot),
        new PlayerId(slot),
        processor,
        DateTimeOffset.UtcNow);
    if (decision.Kind != PacketDecisionKind.Allow)
    {
        throw new InvalidOperationException($"Baseline packet was rejected: {decision}.");
    }
}

double Percentile(double percentile)
{
    int index = (int)Math.Ceiling(percentile * tickNanoseconds.Length) - 1;
    return tickNanoseconds[Math.Clamp(index, 0, tickNanoseconds.Length - 1)];
}

static byte[] Serialize(TerrariaPacket packet)
{
    using var stream = new MemoryStream();
    packet.ToStream(stream, includeHeader: false);
    return stream.ToArray();
}

static long? ReadLinuxContextSwitches()
{
    const string path = "/proc/self/status";
    if (!File.Exists(path))
    {
        return null;
    }

    long total = 0;
    foreach (string line in File.ReadLines(path))
    {
        if (!line.StartsWith("voluntary_ctxt_switches", StringComparison.Ordinal) &&
            !line.StartsWith("nonvoluntary_ctxt_switches", StringComparison.Ordinal))
        {
            continue;
        }

        string value = line[(line.IndexOf(':') + 1)..].Trim();
        total += long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    return total;
}
