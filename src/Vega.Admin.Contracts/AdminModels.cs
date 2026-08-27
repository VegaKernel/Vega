namespace Vega.Admin.Contracts;

public readonly record struct AdminProtocolVersion(int Major, int Minor)
{
    public static AdminProtocolVersion V1 { get; } = new(1, 0);

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed record AdminCapabilityDescriptor(string Name, int Version = 1);

public sealed record AdminProtocolInfo(
    AdminProtocolVersion Version,
    IReadOnlyList<AdminCapabilityDescriptor> Capabilities)
{
    public static AdminProtocolInfo Phase27Initial { get; } = new(
        AdminProtocolVersion.V1,
        [
            new("server.status"),
            new("players.snapshot"),
            new("commands.catalog"),
            new("commands.execute"),
            new("modules.snapshot"),
            new("logs.query"),
            new("chat.snapshot"),
            new("administration.snapshot"),
            new("diagnostics.snapshot")
        ]);
}

public sealed record AdminServerStatusSnapshot(
    string ServerName,
    string Version,
    string LifecycleStage,
    TimeSpan Uptime,
    string? WorldName,
    bool IsWorldReady,
    bool IsNetworkReady,
    int OnlinePlayers,
    int MaximumPlayers,
    double CurrentTicksPerSecond,
    double AverageTicksPerSecondOneMinute,
    DateTimeOffset SampledAt);

public sealed record AdminCommandDescriptor(
    string Name,
    IReadOnlyList<string> Aliases,
    string Syntax,
    IReadOnlyList<string> Permissions,
    bool AllowServer,
    string Owner,
    string? TitleKey = null,
    string? DescriptionKey = null,
    string? HelpKey = null,
    string? FallbackTitle = null,
    string? FallbackDescription = null,
    string? FallbackHelp = null)
{
    public bool AllowPlayers { get; init; } = true;
}

public sealed record AdminCommandRequest(
    string Name,
    IReadOnlyList<string> Arguments,
    bool Silent = false);

public enum AdminCommandExecutionStatus
{
    Success,
    InvalidUsage,
    Forbidden,
    Failed
}

public enum AdminMessageSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public enum AdminScalarKind
{
    Null,
    String,
    Integer,
    Number,
    Boolean,
    DateTimeOffset,
    Duration
}

/// <summary>
/// A protocol-safe scalar formatting argument. The wire contract never carries arbitrary CLR
/// objects; Value is invariant text whose semantic kind is explicit for client-side formatting.
/// </summary>
public sealed record AdminMessageArgument(AdminScalarKind Kind, string? Value);

public sealed record AdminMessageDescriptor(
    string? Key,
    IReadOnlyList<AdminMessageArgument> Arguments,
    AdminMessageSeverity Severity,
    string? FallbackText = null,
    string Source = "vega");

public sealed record AdminCommandResult(
    AdminCommandExecutionStatus Status,
    string? ErrorCode,
    IReadOnlyList<AdminMessageDescriptor> Messages)
{
    public bool IsSuccess => Status == AdminCommandExecutionStatus.Success;
}
