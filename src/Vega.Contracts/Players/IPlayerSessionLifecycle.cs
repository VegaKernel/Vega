namespace Vega.Contracts.Players;

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

/// <summary>Optional authoritative pre-reset notification while the old character still exists.</summary>
public interface IPlayerSessionClosing
{
    event Action<PlayerSessionClosed>? Closing;
}

/// <summary>Optional native first-world-entry boundary, before gameplay and character restoration.</summary>
public interface IPlayerSessionEnteredWorld
{
    event Action<PlayerSnapshot>? EnteredWorld;
}
