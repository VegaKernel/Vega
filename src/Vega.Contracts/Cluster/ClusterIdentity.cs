using System.Text.Json.Serialization;

namespace Vega.Contracts.Cluster;

public readonly record struct ServerId
{
    [JsonConstructor]
    public ServerId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ClusterMessageId(Guid Value)
{
    public static ClusterMessageId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

public readonly record struct ClusterCorrelationId(Guid Value)
{
    public static ClusterCorrelationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
