using System.Diagnostics.CodeAnalysis;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Contracts.Worlds;
using Vega.PluginSdk;

namespace Vega.Core.Modules;

internal sealed partial class HotModuleRegistrationScope
{
    private sealed class PluginPlayers(
        HotModuleRegistrationScope owner,
        IPlayerDirectory? directory,
        IPlayerMessenger? messenger,
        IPlayerOperations? operations,
        IPlayerSessionLifecycle? lifecycle, WorldId? worldId) : IPluginPlayers
    {
        public IReadOnlyList<PlayerSnapshot> Online => directory?.SnapshotOnlinePlayers() ?? [];
        public IPluginPlayerActions Actions { get; } = new PluginPlayerActions(owner, directory, worldId,
            operations as IPlayerVitalOperations, operations as Vega.Contracts.Items.IPlayerItemDelivery);

        public bool TryGet(PlayerId id, [NotNullWhen(true)] out PlayerSnapshot? player)
        {
            if (directory is not null)
            {
                return directory.TryGet(id, out player) && player is not null;
            }

            player = null;
            return false;
        }

        public bool TryFindExact(string name, [NotNullWhen(true)] out PlayerSnapshot? player)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (directory is not null)
            {
                return directory.TryFindExact(name, out player) && player is not null;
            }

            player = null;
            return false;
        }

        public void Message(PlayerId id, string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            owner.TrackTask(SendMessageCoreAsync(id, message));
        }

        public void Message(PlayerSnapshot player, string message)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            owner.TrackTask(SendMessageCoreAsync(player.Id, message, player));
        }

        public ValueTask<PlayerOperationResult> MessageAsync(
            PlayerId id,
            string message,
            CancellationToken cancellationToken = default) =>
            messenger?.SendMessageAsync(id, message, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        public ValueTask<PlayerOperationResult> MessageAsync(PlayerSnapshot player, string message, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(player);
            ArgumentNullException.ThrowIfNull(message);
            return RunExactAsync(player, messenger?.SupportsSessionBoundMessages == true,
                token => messenger!.SendMessageAsync(player, message, token), cancellationToken);
        }

        private async Task SendMessageCoreAsync(PlayerId id, string message, PlayerSnapshot? expected = null)
        {
            try
            {
                PlayerOperationResult result = await (expected is null ? MessageAsync(id, message, owner.lifetime.Token) :
                    MessageAsync(expected, message, owner.lifetime.Token))
                    .ConfigureAwait(false);
                if (!result.IsSuccess)
                {
                    owner.logger.Log(OperationsLogLevel.Warning, owner.logSource,
                        $"Player message was not applied: {result.ErrorCode ?? result.Status.ToString()}.");
                }
            }
            catch (OperationCanceledException) when (owner.lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                owner.logger.Log(OperationsLogLevel.Error, owner.logSource,
                    "Player message failed.", ex);
            }
        }

        public ValueTask<PlayerOperationResult> KickAsync(
            PlayerId id,
            string reason,
            CancellationToken cancellationToken = default) =>
            operations?.KickAsync(id, reason, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        public ValueTask<PlayerOperationResult> KickAsync(PlayerSnapshot player, string reason, CancellationToken cancellationToken = default) =>
            RunExactAsync(player, operations?.SupportsSessionBoundKicks == true, token => operations!.KickAsync(player, reason, token), cancellationToken);

        public ValueTask<PlayerOperationResult> TeleportAsync(
            PlayerId id,
            PlayerPosition destination,
            CancellationToken cancellationToken = default) =>
            operations?.TeleportAsync(id, destination, cancellationToken) ??
            ValueTask.FromResult(PlayerOperationResult.DispatcherUnavailable());

        public ValueTask<PlayerOperationResult> TeleportAsync(PlayerSnapshot player, PlayerPosition destination, CancellationToken cancellationToken = default) =>
            RunExactAsync(player, operations is IPlayerNavigation, token => ((IPlayerNavigation)operations!).ToPositionAsync(player, destination, token), cancellationToken);

        private async ValueTask<PlayerOperationResult> RunExactAsync(PlayerSnapshot player, bool supported,
            Func<CancellationToken, ValueTask<PlayerOperationResult>> operation, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(player);
            if (!supported) return PlayerOperationResult.DispatcherUnavailable();
            if (player.SessionId is not { } session || (worldId is { } hosted && player.WorldId != hosted) ||
                directory is null || !directory.TryGet(player.Id, out var current) || current is null || current.SessionId != session || current.WorldId != player.WorldId)
                return PlayerOperationResult.NotFound();
            try { return await owner.RunTrackedOperationAsync(operation, token, offload: false, discardAfterRetirement: false).ConfigureAwait(false); }
            catch (OperationCanceledException) { return PlayerOperationResult.Cancelled(); }
            catch (PluginScopeBusyException) { return PlayerOperationResult.DispatcherUnavailable(); }
        }

        public void OnJoined(Action<PlayerSessionOpened> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is null)
            {
                return;
            }

            Action<PlayerSessionOpened> guarded = opened =>
                owner.executionGuard.TryInvoke("players.joined", handler, opened);
            owner.Subscribe(() => lifecycle.Opened += guarded, () => lifecycle.Opened -= guarded);
        }

        public void OnLeft(Action<PlayerSessionClosed> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is null)
            {
                return;
            }

            Action<PlayerSessionClosed> guarded = closed =>
                owner.executionGuard.TryInvoke("players.left", handler, closed);
            owner.Subscribe(() => lifecycle.Closed += guarded, () => lifecycle.Closed -= guarded);
        }

        public void OnEnteredWorld(Action<PlayerSnapshot> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is not IPlayerSessionEnteredWorld entering) return;
            Action<PlayerSnapshot> guarded = player => owner.executionGuard.TryInvoke("players.entered-world", handler, player);
            owner.Subscribe(() => entering.EnteredWorld += guarded, () => entering.EnteredWorld -= guarded);
        }

        public void OnClosing(Action<PlayerSessionClosed> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (lifecycle is not IPlayerSessionClosing closing) return;
            Action<PlayerSessionClosed> guarded = player => owner.executionGuard.TryInvoke("players.closing", handler, player);
            owner.Subscribe(() => closing.Closing += guarded, () => closing.Closing -= guarded);
        }
    }
}
