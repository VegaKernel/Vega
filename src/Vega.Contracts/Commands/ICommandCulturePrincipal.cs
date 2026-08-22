namespace Vega.Contracts.Commands;

public interface ICommandCulturePrincipal : ICommandPrincipal
{
    string CultureSubjectId { get; }
}
