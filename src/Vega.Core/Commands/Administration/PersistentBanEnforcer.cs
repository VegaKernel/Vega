using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Operations;
using Vega.Contracts.Players;

namespace Vega.Core.Commands.Administration;

public sealed class PersistentBanEnforcer : IDisposable
{
    private readonly IGlobalBanReplica bans;
    private readonly IPlayerDirectory players;
    private readonly IPlayerOperations playerOperations;
    private readonly IPlayerSessionLifecycle sessions;
    private readonly IOperationsLogger logger;
    private readonly CancellationTokenSource shutdown = new();

    public PersistentBanEnforcer(
        IGlobalBanReplica bans,
        IPlayerDirectory players,
        IPlayerOperations playerOperations,
        IPlayerSessionLifecycle sessions,
        IOperationsLogger logger)
    {
        this.bans = bans ?? throw new ArgumentNullException(nameof(bans));
        this.players = players ?? throw new ArgumentNullException(nameof(players));
        this.playerOperations = playerOperations ?? throw new ArgumentNullException(nameof(playerOperations));
        this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        sessions.Opened += OnSessionOpened;
    }

    public void Dispose()
    {
        sessions.Opened -= OnSessionOpened;
        shutdown.Cancel();
        shutdown.Dispose();
    }

    private void OnSessionOpened(PlayerSessionOpened opened)
    {
        _ = Task.Run(() => EnforceAsync(opened), shutdown.Token);
    }

    private async Task EnforceAsync(PlayerSessionOpened opened)
    {
        try
        {
            PlayerSnapshot? player = players.SnapshotOnlinePlayers()
                .FirstOrDefault(candidate => candidate.Id == opened.PlayerId);
            IReadOnlyList<GlobalBanCreated> active = bans.SnapshotActive(DateTimeOffset.UtcNow);
            GlobalBanCreated? ban = active.FirstOrDefault(candidate => Matches(candidate.Subject, opened.PlayerName, player?.RemoteEndpoint));
            if (ban is null)
            {
                return;
            }

            PlayerOperationResult result = await playerOperations
                .KickAsync(opened.PlayerId, $"Banned: {ban.Reason}", shutdown.Token)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                logger.Log(
                    OperationsLogLevel.Warning,
                    "bans",
                    $"Active ban matched '{opened.PlayerName}', but the reconnect could not be disconnected ({result.ErrorCode ?? "unknown"}).");
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.Log(
                OperationsLogLevel.Error,
                "bans",
                $"Failed to enforce persistent bans for '{opened.PlayerName}'.",
                ex);
        }
    }

    private static bool Matches(GlobalBanSubject subject, string playerName, string? remoteEndpoint)
    {
        if (subject.Kind == GlobalBanSubjectKind.PlayerName)
        {
            return string.Equals(subject.Value, playerName, StringComparison.OrdinalIgnoreCase);
        }

        return subject.Kind == GlobalBanSubjectKind.IpAddress &&
            BanCommand.TryGetIpAddress(remoteEndpoint, out string? address) &&
            string.Equals(subject.Value, address, StringComparison.OrdinalIgnoreCase);
    }
}
