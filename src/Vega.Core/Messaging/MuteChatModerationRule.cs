using System.Net;
using System.Net.Sockets;
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
            new MuteIdentity(
                context.Sender.Name,
                account?.Name,
                context.Sender.Connection?.ClientUuid,
                ExtractIp(context.Sender.Connection?.EffectiveEndpoint ?? context.Sender.RemoteEndpoint)),
            time.GetUtcNow());
        return mute is null
            ? ChatModerationDecision.Allow
            : ChatModerationDecision.Reject(
                $"You are muted until {mute.ExpiresAt:u}. Reason: {mute.Reason}");
    }

    private static string? ExtractIp(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (IPAddress.TryParse(endpoint, out IPAddress? address)) return UseIpv4(address);
        return IPEndPoint.TryParse(endpoint, out IPEndPoint? parsed)
            ? UseIpv4(parsed.Address)
            : null;
    }

    private static string? UseIpv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork ? address.ToString() : null;
}
