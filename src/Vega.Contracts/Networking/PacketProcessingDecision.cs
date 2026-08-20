namespace Vega.Contracts.Networking;

public enum PacketDecisionKind
{
    Continue,
    Handled,
    Reject
}

public sealed record PacketProcessingDecision(PacketDecisionKind Kind, string? Reason = null);
