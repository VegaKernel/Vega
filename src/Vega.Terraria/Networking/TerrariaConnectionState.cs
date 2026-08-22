namespace Vega.Terraria.Networking;

/// <summary>
/// Named values from Terraria's server-side RemoteClient.State join state machine.
/// Intermediate values remain Terraria-owned implementation details until Vega needs to model them explicitly.
/// </summary>
public enum TerrariaConnectionState
{
    PasswordChallenge = -1,
    AwaitingHello = 0,
    ProtocolAccepted = 1,
    WorldRequested = 2,
    SendingWorld = 3,
    FullyConnected = 10
}
