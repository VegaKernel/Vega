namespace Vega.Contracts.Events;

public readonly record struct EventCancellation
{
    public EventCancellation(string code, string? message = null, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Cancellation code must not be empty.", nameof(code));
        }

        Code = code;
        Message = message;
        Source = source;
    }

    public string Code { get; }

    public string? Message { get; }

    public string? Source { get; }
}
