namespace Vega.Contracts.Diagnostics;

/// <summary>Low-overhead network event emitted only when a diagnostic listener is active.</summary>
public sealed record NetworkPacketDiagnosticEvent(
    string Direction,
    int? PacketId,
    int Size,
    int? Slot,
    DateTimeOffset Timestamp);

/// <summary>Timing sample for one packet processor invocation.</summary>
public sealed record PacketProcessorDiagnosticEvent(
    string Processor,
    string PacketType,
    string Direction,
    string Connection,
    string? Player,
    double ElapsedMicroseconds,
    string Decision,
    string? Reason);
