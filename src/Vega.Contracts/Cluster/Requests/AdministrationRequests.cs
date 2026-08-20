using Vega.Contracts.Commands;
using Vega.Contracts.Runtime;

namespace Vega.Contracts.Cluster.Requests;

public sealed record ExecuteAdministrativeCommand(string CommandText) :
    IClusterRequest<AdministrativeCommandResponse>
{
    public string Schema => "vega.cluster.admin.execute-command";

    public int SchemaVersion => 1;
}

public sealed record AdministrativeCommandResponse(
    CommandExecutionStatus Status,
    string? ErrorCode,
    IReadOnlyList<string> Messages) : IClusterResponse
{
    public string Schema => "vega.cluster.admin.command-response";

    public int SchemaVersion => 1;
}

public sealed record ServerStatusRequest : IClusterRequest<ServerStatusResponse>
{
    public string Schema => "vega.cluster.status.request";

    public int SchemaVersion => 1;
}

public sealed record ServerStatusResponse(
    ServerId ServerId,
    RuntimeLifecycleStage Stage,
    TimeSpan Uptime,
    int OnlinePlayers,
    string? WorldName) : IClusterResponse
{
    public string Schema => "vega.cluster.status.response";

    public int SchemaVersion => 1;
}
