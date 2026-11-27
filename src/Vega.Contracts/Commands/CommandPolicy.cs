using Vega.Contracts.Players;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Commands;

/// <summary>Metadata for a player command after dispatch authorization, before execution. Never contains arguments or credentials.</summary>
public readonly record struct PlayerCommandPolicyContext(WorldId WorldId, string CommandName, string InvokedName,
    PlayerId PlayerId, PlayerSessionId SessionId);

/// <summary>A plugin can decline execution but cannot grant permission or change the invocation.</summary>
public readonly record struct CommandPolicyDecision(bool Rejected, string? Reason = null)
{
    public static CommandPolicyDecision Allow => default;
    public static CommandPolicyDecision Reject(string? reason = null) => new(true, reason);
}

/// <summary>Host policy boundary. Ordinary plugins use the lifetime-managed SDK, not the dispatcher.</summary>
public interface ICommandPolicyEvents
{
    event Func<PlayerCommandPolicyContext, CommandPolicyDecision>? BeforePlayerCommand;
}
