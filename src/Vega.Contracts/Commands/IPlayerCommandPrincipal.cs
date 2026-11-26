using Vega.Contracts.Players;

namespace Vega.Contracts.Commands;

public interface IPlayerCommandPrincipal : ICommandCulturePrincipal
{
    PlayerId PlayerId { get; }

    PlayerSessionId? SessionId => null;

    string PlayerName { get; }

    string? AccountName { get; }
}
