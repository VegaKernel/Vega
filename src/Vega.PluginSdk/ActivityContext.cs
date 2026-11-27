using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;

namespace Vega.PluginSdk;

/// <summary>World-local observations and restrictive player command/public chat policies.</summary>
public interface IPluginActivity
{
    bool AuthenticationAvailable { get; }
    bool PublicChatAvailable { get; }
    bool CommandCompletionAvailable { get; }

    bool PlayerCommandPolicyAvailable => false;
    bool PublicChatPolicyAvailable => false;

    /// <summary>Checks the exact current authentication tenure without exposing credentials.</summary>
    bool TryGetAuthentication(PlayerSnapshot player, out PlayerAccountBinding binding);

    /// <summary>Obsolete queued establishments are omitted; revalidate before subsequent operations.</summary>
    void OnAuthenticationEstablished(Action<PlayerAccountBinding> handler);
    /// <summary>Historical tenure end, even if the connection slot has already been reused.</summary>
    void OnAuthenticationEnded(Action<PlayerAuthenticationEnded> handler);
    void OnPublicChatAccepted(Action<PublicChatAcceptedObservation> handler);
    /// <summary>Only authorized executed commands returning a result, including unsuccessful results.</summary>
    void OnCommandCompleted(Action<CommandCompletedObservation> handler);

    /// <summary>Synchronous, restrictive policy after dispatch permission checks. No arguments, console commands, or authority grants.</summary>
    void BeforePlayerCommand(Func<PlayerCommandPolicyContext, CommandPolicyDecision> policy) =>
        throw new NotSupportedException("Player command policy is unavailable in this host.");

    /// <summary>Synchronous ordinary chat transform/veto; built-in transforms and moderation still apply.</summary>
    void BeforePublicChat(Func<PublicChatPolicyContext, PublicChatPolicyDecision> policy) =>
        throw new NotSupportedException("Public chat policy is unavailable in this host.");
}
