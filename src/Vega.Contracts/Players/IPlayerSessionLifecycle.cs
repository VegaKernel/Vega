namespace TShockNG.Contracts.Players;

public readonly record struct PlayerSessionOpened(
	PlayerSessionId SessionId,
	PlayerId PlayerId,
	string PlayerName,
	DateTimeOffset Timestamp);

public readonly record struct PlayerSessionClosed(
	PlayerSessionId SessionId,
	PlayerId PlayerId,
	string PlayerName,
	DateTimeOffset Timestamp);

public interface IPlayerSessionLifecycle
{
	event Action<PlayerSessionOpened>? Opened;

	event Action<PlayerSessionClosed>? Closed;
}
