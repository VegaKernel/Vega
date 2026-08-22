using Vega.Contracts.Accounts;
using Vega.Contracts.Messaging;
using Vega.Contracts.Moderation;

namespace Vega.Core.Messaging;

public sealed class MuteChatModerationRule(
    IPlayerAccountSessionService accounts,
    IMuteService mutes,
    TimeProvider? timeProvider = null) : IChatModerationRule
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public ChatModerationDecision Evaluate(ChatMessageContext context, string content)
    {
        accounts.TryGetAccount(context.Sender.Id, out AccountSnapshot? account);
        MuteSnapshot? mute = mutes.FindActive(
            context.Sender.Name,
            account?.Name,
            time.GetUtcNow());
        return mute is null
            ? ChatModerationDecision.Allow
            : ChatModerationDecision.Reject(
                $"You are muted until {mute.ExpiresAt:u}. Reason: {mute.Reason}");
    }
}
