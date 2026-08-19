namespace Vega.Contracts.Events;

public readonly record struct EventCancellation(
    string Code,
    string? Message = null,
    string? Source = null)
{
    public EventCancellation
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            throw new ArgumentException("Cancellation code must not be empty.", nameof(Code));
        }
    }
}
