namespace Vega.Contracts.Networking;

public enum PacketDecisionKind
{
    Allow,
    Reject,
    Violation
}

public sealed record PacketProcessingDecision(PacketDecisionKind Kind, string? Reason = null)
{
    public static PacketProcessingDecision Allow { get; } = new(PacketDecisionKind.Allow);

    public static PacketProcessingDecision Reject(string reason) =>
        new(PacketDecisionKind.Reject, reason);

    public static PacketProcessingDecision Violation(string reason) =>
        new(PacketDecisionKind.Violation, reason);
}
