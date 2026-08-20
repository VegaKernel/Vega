namespace TShockNG.Contracts.Networking;

/// <summary>
/// Identifies the runtime connection that owns a packet independently from authenticated player identity.
/// </summary>
public readonly record struct ConnectionId(int Slot);
