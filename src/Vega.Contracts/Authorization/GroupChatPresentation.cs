namespace Vega.Contracts.Authorization;

/// <summary>Optional display-only metadata; it never participates in authorization.</summary>
public sealed record GroupChatPresentation(string Prefix = "", string Suffix = "", string? Color = null)
{
    public static GroupChatPresentation Default { get; } = new();

    public bool IsValid => ValidAffix(Prefix) && ValidAffix(Suffix) &&
        (Color is null || Color.Length == 6 && Color.All(Uri.IsHexDigit));

    private static bool ValidAffix(string? value) =>
        value is not null && value.Length <= 64 && !value.Any(char.IsControl);
}
