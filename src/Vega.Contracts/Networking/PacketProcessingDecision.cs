namespace Vega.Contracts.Networking;

public enum PacketDecisionKind
{
    Continue,
    Handled,
    Reject,
    Violation
}

public sealed record PacketProcessingDecision(PacketDecisionKind Kind, string? Reason = null);
