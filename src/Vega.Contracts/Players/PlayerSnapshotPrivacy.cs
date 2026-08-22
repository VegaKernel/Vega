namespace Vega.Contracts.Players;

public static class PlayerSnapshotPrivacy
{
    public static PlayerSnapshot RedactSensitive(PlayerSnapshot player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return player with
        {
            RemoteEndpoint = null,
            Connection = player.Connection is null
                ? null
                : player.Connection with
                {
                    TransportEndpoint = null,
                    EffectiveEndpoint = null,
                    ClientUuid = null
                }
        };
    }
}
