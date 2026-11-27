using Vega.Contracts.Accounts;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Players;
using Vega.Contracts.Threading;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginActivity(HotModuleRegistrationScope owner, WorldId? worldId,
        IPlayerDirectory? players, IGameThreadDispatcher? gameThread,
        IPlayerAccountSessionService? authentication, IPublicChatObservations? publicChat,
        ICommandCompletionObservations? commands, ICommandPolicyEvents? commandPolicy) : IPluginActivity
    {
        public bool AuthenticationAvailable => worldId.HasValue && players is not null && gameThread is not null && authentication is not null;
        public bool PublicChatAvailable => worldId.HasValue && gameThread is not null && publicChat is not null;
        public bool CommandCompletionAvailable => worldId.HasValue && gameThread is not null && commands is not null;
        public bool PlayerCommandPolicyAvailable => worldId.HasValue && players is not null && commandPolicy is not null;
        public bool PublicChatPolicyAvailable => worldId.HasValue && players is not null && publicChat is IPublicChatPolicyEvents;

        public bool TryGetAuthentication(PlayerSnapshot player, out PlayerAccountBinding binding)
        {
            binding = default;
            return !owner.lifetime.IsCancellationRequested && AuthenticationAvailable && player.WorldId == worldId &&
                player.SessionId is { } session && players!.TryGet(player.Id, out PlayerSnapshot? current) &&
                current is not null && current.WorldId == worldId && current.SessionId == session &&
                authentication!.TryGetBinding(current, out binding);
        }

        public void OnAuthenticationEstablished(Action<PlayerAccountBinding> handler)
        {
            Ensure(AuthenticationAvailable);
            Subscribe("authentication-established", handler,
                callback => authentication!.BindingEstablished += callback,
                callback => authentication!.BindingEstablished -= callback,
                // Authentication may commit before the native player enters the world. Validate
                // tenure directly; the host-local account service belongs to this world.
                binding => authentication!.TryRunWithBinding(binding, static () => true, out bool active) && active);
        }

        public void OnAuthenticationEnded(Action<PlayerAuthenticationEnded> handler)
        {
            Ensure(AuthenticationAvailable);
            Subscribe("authentication-ended", handler,
                callback => authentication!.AuthenticationEnded += callback,
                callback => authentication!.AuthenticationEnded -= callback);
        }

        public void OnPublicChatAccepted(Action<PublicChatAcceptedObservation> handler)
        {
            Ensure(PublicChatAvailable);
            Subscribe("public-chat-accepted", handler,
                callback => publicChat!.Accepted += callback,
                callback => publicChat!.Accepted -= callback,
                value => value.WorldId == worldId);
        }

        public void OnCommandCompleted(Action<CommandCompletedObservation> handler)
        {
            Ensure(CommandCompletionAvailable);
            Subscribe("command-completed", handler,
                callback => commands!.Completed += callback,
                callback => commands!.Completed -= callback,
                value => value.WorldId == worldId);
        }

        public void BeforePlayerCommand(Func<PlayerCommandPolicyContext, CommandPolicyDecision> policy)
        {
            Ensure(PlayerCommandPolicyAvailable);
            SubscribePolicy("before-player-command", policy,
                callback => commandPolicy!.BeforePlayerCommand += callback,
                callback => commandPolicy!.BeforePlayerCommand -= callback,
                value => IsCurrentSession(value.WorldId, value.PlayerId, value.SessionId));
        }

        public void BeforePublicChat(Func<PublicChatPolicyContext, PublicChatPolicyDecision> policy)
        {
            Ensure(PublicChatPolicyAvailable);
            var events = (IPublicChatPolicyEvents)publicChat!;
            SubscribePolicy("before-public-chat", policy,
                callback => events.BeforePublicChat += callback,
                callback => events.BeforePublicChat -= callback,
                value => IsCurrentSession(value.WorldId, value.PlayerId, value.SessionId),
                decision =>
                {
                    if (!decision.Rejected && decision.Replacement is { } replacement && !Messaging.ChatPipeline.IsValidReplacement(replacement))
                        throw new ArgumentException("Public chat replacement must be ordinary non-empty text of at most 1024 characters, without control characters.");
                });
        }

        private bool IsCurrentSession(WorldId world, PlayerId player, PlayerSessionId session) =>
            world == worldId && players!.TryGet(player, out PlayerSnapshot? current) && current is not null &&
            current.WorldId == world && current.SessionId == session;

        // Decisions must run inline at the existing command/chat boundary; they never wait for
        // the game-thread queue. A fault, retirement, or stale session leaves built-in policy intact.
        private void SubscribePolicy<T, TResult>(string operation, Func<T, TResult> policy,
            Action<Func<T, TResult>> attach, Action<Func<T, TResult>> detach, Func<T, bool> accept,
            Action<TResult>? validate = null) where TResult : struct
        {
            ArgumentNullException.ThrowIfNull(policy);
            Func<T, TResult>? activeHandler = value =>
            {
                TResult candidate = policy(value);
                validate?.Invoke(candidate);
                return candidate;
            };
            TResult Guarded(T value)
            {
                if (owner.lifetime.IsCancellationRequested || !owner.executionGuard.AcceptsCallbacks || !accept(value)) return default;
                return Volatile.Read(ref activeHandler) is { } live ? owner.executionGuard.InvokePolicy(operation, live, value) : default;
            }
            owner.Subscribe(() => attach(Guarded), () =>
            {
                Interlocked.Exchange(ref activeHandler, null);
                detach(Guarded);
            });
        }

        private void Subscribe<T>(string operation, Action<T> handler, Action<Action<T>> attach,
            Action<Action<T>> detach, Func<T, bool>? accept = null)
        {
            ArgumentNullException.ThrowIfNull(handler);
            Action<T>? activeHandler = handler;
            void Guarded(T value)
            {
                if (owner.lifetime.IsCancellationRequested || !owner.executionGuard.AcceptsCallbacks) return;
                // Always defer, including a publication from the game thread, so a plugin cannot
                // hold an account/command/native hook's stack while observing a completed operation.
                gameThread!.TryPost(operation, () =>
                {
                    if (!owner.lifetime.IsCancellationRequested && Volatile.Read(ref activeHandler) is { } liveHandler &&
                        (accept is null || accept(value)))
                        owner.executionGuard.TryInvoke(operation, liveHandler, value);
                });
            }
            owner.Subscribe(() => attach(Guarded), () =>
            {
                Interlocked.Exchange(ref activeHandler, null);
                detach(Guarded);
            });
        }

        private static void Ensure(bool supported)
        {
            if (!supported) throw new NotSupportedException("This activity observation is unavailable in the current host.");
        }
    }
}
