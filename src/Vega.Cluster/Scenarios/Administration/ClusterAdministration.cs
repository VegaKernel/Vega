using Vega.Contracts.Operations;
using Vega.Contracts.Players;
using Vega.Cluster.Scenarios.Presence;
using Vega.Contracts.Cluster;
using Vega.Contracts.Cluster.Commands;
using Vega.Contracts.Cluster.Requests;

namespace Vega.Cluster.Scenarios.Administration;

public sealed class DisconnectPlayerHandler(
    IPlayerDirectory players,
    IPlayerOperations operations) : IClusterCommandHandler<DisconnectPlayer>
{
    public async ValueTask HandleAsync(
        ClusterEnvelope<DisconnectPlayer> envelope,
        CancellationToken cancellationToken)
    {
        if (players.TryFindExact(envelope.Message.PlayerName, out PlayerSnapshot? player) && player is not null)
        {
            await operations
                .KickAsync(player.Id, envelope.Message.Reason, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

public sealed class AdministrativeCommandHandler(IOperationsCommandService commands) :
    IClusterRequestHandler<ExecuteAdministrativeCommand, AdministrativeCommandResponse>
{
    public async ValueTask<AdministrativeCommandResponse> HandleAsync(
        ClusterEnvelope<ExecuteAdministrativeCommand> envelope,
        CancellationToken cancellationToken)
    {
        OperationsActionResult result = await commands
            .ExecuteAsync(envelope.Message.CommandText, cancellationToken)
            .ConfigureAwait(false);
        return new AdministrativeCommandResponse(result.Status, result.ErrorCode, result.Messages);
    }
}

public sealed class ServerStatusHandler(
    ServerId serverId,
    IOperationsSnapshotProvider snapshots) :
    IClusterRequestHandler<ServerStatusRequest, ServerStatusResponse>
{
    public ValueTask<ServerStatusResponse> HandleAsync(
        ClusterEnvelope<ServerStatusRequest> envelope,
        CancellationToken cancellationToken)
    {
        OperationsSnapshot snapshot = snapshots.Capture(new OperationsLogFilter(Limit: 0));
        return ValueTask.FromResult(new ServerStatusResponse(
            serverId,
            snapshot.Server.LifecycleStage,
            snapshot.Server.Uptime,
            snapshot.Server.OnlinePlayers,
            snapshot.Server.WorldName));
    }
}

public sealed class ClusterAdministrationClient(
    IClusterRequestSender requests,
    ClusterMessageFactory messages)
{
    public ValueTask<ClusterRequestResult<AdministrativeCommandResponse>> ExecuteAsync(
        ServerId target,
        string commandText,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        requests.RequestAsync<ExecuteAdministrativeCommand, AdministrativeCommandResponse>(
            target,
            messages.Create(
                new ExecuteAdministrativeCommand(commandText),
                ClusterCorrelationId.New()),
            timeout,
            cancellationToken);

    public async ValueTask<IReadOnlyList<ClusterRequestResult<ServerStatusResponse>>> QueryOnlineServersAsync(
        ServerPresenceDirectory presence,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ClusterRequestResult<ServerStatusResponse>>();
        foreach (ServerPresenceSnapshot server in presence.SnapshotOnline())
        {
            results.Add(await requests
                .RequestAsync<ServerStatusRequest, ServerStatusResponse>(
                    server.ServerId,
                    messages.Create(new ServerStatusRequest(), ClusterCorrelationId.New()),
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        return Array.AsReadOnly(results.ToArray());
    }
}
