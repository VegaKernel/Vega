namespace Vega.Contracts.Networking;

/// <summary>Vanilla's persisted, client-supplied opaque metadata token, not an authentication credential.</summary>
public static class ClientUuidToken
{
    public const int MaximumLength = 128;

    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumLength;
}
