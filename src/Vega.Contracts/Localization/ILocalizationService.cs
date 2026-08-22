using Vega.Contracts.Messaging;

namespace Vega.Contracts.Localization;

public interface ILocalizationService
{
    string Format(MessageDescriptor message, PlayerCulture culture);

    IDisposable RegisterCatalog(
        string source,
        string culture,
        IReadOnlyDictionary<string, string> messages);
}
