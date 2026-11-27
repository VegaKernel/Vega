using System.Text.Json;
using Vega.Contracts.Cluster;

namespace Vega.Cluster.Transport;

/// <summary>Only secret environment-variable names are stored in this server configuration.</summary>
public sealed record ClusterPeerOptions(string ServerId, string Endpoint, string SendTokenEnvironmentVariable, string ReceiveTokenEnvironmentVariable)
{
    public ClusterServerRole Role { get; init; }
}

public sealed record ClusterTransportOptions(string ServerId, int ListenPort, ClusterPeerOptions[] Peers)
{
    public ClusterServerRole Role { get; init; }
    public string? DisplayName { get; init; }

    public static ClusterTransportOptions? Load(string path)
    {
        if (!File.Exists(path)) return null;
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > 64 * 1024) throw new InvalidDataException("Cluster configuration exceeds its size limit.");
        return JsonSerializer.Deserialize<ClusterTransportOptions>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Invalid cluster transport configuration.");
    }
}
