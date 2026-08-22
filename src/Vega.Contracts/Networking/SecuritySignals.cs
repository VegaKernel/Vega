namespace Vega.Contracts.Networking;

public enum SecuritySignalExposure
{
    Redacted,
    Sensitive
}

public sealed record SecuritySignal(
    DateTimeOffset Timestamp,
    ConnectionId ConnectionId,
    byte PacketId,
    PacketDecisionKind Decision,
    string Reason,
    string? TransportEndpoint,
    string? EffectiveEndpoint,
    ConnectionForwardingProvenance ForwardingProvenance);

public interface ISecuritySignalRecorder
{
    void Record(SecuritySignal signal);
}

public interface ISecuritySignalReader
{
    IReadOnlyList<SecuritySignal> Read(
        SecuritySignalExposure exposure,
        int limit = 100);
}
