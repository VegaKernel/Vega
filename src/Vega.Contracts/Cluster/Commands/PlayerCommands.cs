namespace Vega.Contracts.Cluster.Commands;

public sealed record DisconnectPlayer(string PlayerName, string Reason) : IClusterCommand
{
    public string Schema => "vega.cluster.player.disconnect";

    public int SchemaVersion => 1;
}
