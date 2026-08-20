namespace Vega.Cluster.Scenarios.Transfer;

/// <summary>
/// Keeps player transfer unavailable until simpler cluster semantics have production validation.
/// </summary>
public sealed record PlayerTransferReadinessGate(
    bool PresenceValidatedInProduction,
    bool BanReplicationValidatedInProduction,
    bool AuthorizationValidatedInProduction,
    bool ChatValidatedInProduction)
{
    public bool IsEnabled =>
        PresenceValidatedInProduction &&
        BanReplicationValidatedInProduction &&
        AuthorizationValidatedInProduction &&
        ChatValidatedInProduction;

    public string? BlockingReason => IsEnabled
        ? null
        : "Player transfer remains disabled until presence, ban, authorization, and chat semantics are validated in production.";
}
