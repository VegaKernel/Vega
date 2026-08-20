namespace Vega.Contracts.Messaging;

/// <summary>
/// Semantic severity of a message before a concrete output adapter renders it.
/// </summary>
public enum MessageSeverity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Describes a message without deciding recipient, culture, rendering backend, or delivery mechanism.
/// </summary>
/// <param name="Key">Stable localization key owned by the producing module/domain.</param>
/// <param name="Arguments">Formatting arguments interpreted by the localization layer.</param>
/// <param name="Severity">Semantic output severity.</param>
/// <param name="Source">Stable module/domain localization namespace. Core messages use <c>tshock</c>.</param>
/// <param name="DefaultText">Optional source/default text used when no localization catalog can resolve <paramref name="Key"/>.</param>
public sealed record MessageDescriptor(
    string Key,
    IReadOnlyList<object?> Arguments,
    MessageSeverity Severity = MessageSeverity.Info,
    string Source = "tshock",
    string? DefaultText = null)
{
    public static MessageDescriptor Info(string key, params object?[] arguments) =>
        new(key, arguments, MessageSeverity.Info);

    public static MessageDescriptor Success(string key, params object?[] arguments) =>
        new(key, arguments, MessageSeverity.Success);

    public static MessageDescriptor Warning(string key, params object?[] arguments) =>
        new(key, arguments, MessageSeverity.Warning);

    public static MessageDescriptor Error(string key, params object?[] arguments) =>
        new(key, arguments, MessageSeverity.Error);

    public MessageDescriptor WithSource(string source) => this with { Source = source };

    public MessageDescriptor WithDefaultText(string defaultText) => this with { DefaultText = defaultText };
}
