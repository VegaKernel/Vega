using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>Read-only, world-local observations. Notifications are best-effort game-thread work,
/// never a policy override, authorization grant, or delivery receipt.</summary>
public interface IPluginActivity
{
    bool AuthenticationAvailable { get; }
    bool PublicChatAvailable { get; }
    bool CommandCompletionAvailable { get; }

    /// <summary>Checks the exact current authentication tenure without exposing credentials.</summary>
    bool TryGetAuthentication(PlayerSnapshot player, out PlayerAccountBinding binding);

    /// <summary>Obsolete queued establishments are omitted; revalidate before subsequent operations.</summary>
    void OnAuthenticationEstablished(Action<PlayerAccountBinding> handler);
    /// <summary>Historical tenure end, even if the connection slot has already been reused.</summary>
    void OnAuthenticationEnded(Action<PlayerAuthenticationEnded> handler);
    void OnPublicChatAccepted(Action<PublicChatAcceptedObservation> handler);
    /// <summary>Only authorized executed commands returning a result, including unsuccessful results.</summary>
    void OnCommandCompleted(Action<CommandCompletedObservation> handler);
}
