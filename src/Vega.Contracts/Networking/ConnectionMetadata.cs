namespace Vega.Contracts.Networking;

public enum ClientPlatformKind
{
    Unknown,
    Mobile,
    Stadia,
    Xbox,
    PlayStation,
    Editor,
    Switch,
    Pc,
    Unrecognized
}

public readonly record struct ClientPlatformInfo(
    ClientPlatformKind Kind,
    byte? ReportedCode = null)
{
    public static ClientPlatformInfo Unknown { get; } = new(ClientPlatformKind.Unknown);
}

public enum ConnectionForwardingProvenance
{
    Direct,
    TrustedForwarder,
    UntrustedForwardingMetadata
}

public enum ConnectionLoginPhase
{
    Connected,
    ProtocolAccepted,
    WorldRequested,
    SendingWorld,
    FullyConnected
}

public sealed record ConnectionMetadataSnapshot(
    ConnectionId ConnectionId,
    string? TransportEndpoint,
    string? EffectiveEndpoint,
    ConnectionForwardingProvenance ForwardingProvenance,
    ClientPlatformInfo Platform,
    string? ClientUuid,
    ConnectionLoginPhase LoginPhase);
