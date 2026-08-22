using System.Globalization;
using Vega.Contracts.Localization;
using Vega.Contracts.Messaging;

namespace Vega.Core.Localization;

public sealed class LocalizationService : ILocalizationService, IPlayerCultureService
{
    private readonly object sync = new();
    private readonly Dictionary<(string Source, string Culture), CatalogRegistration> catalogs = [];
    private readonly Dictionary<string, PlayerCulture> sessionCultures = new(StringComparer.Ordinal);
    private long nextRegistrationId;

    public LocalizationService(string serverCulture, IEnumerable<string>? supportedCultures = null)
    {
        ServerCulture = Normalize(serverCulture);
        PlayerCulture[] configured = (supportedCultures ?? ["en-US", "ru-RU"])
            .Append(ServerCulture.Name)
            .Select(Normalize)
            .DistinctBy(static culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static culture => culture.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        AvailableCultures = Array.AsReadOnly(configured);
    }

    public PlayerCulture ServerCulture { get; }

    public IReadOnlyList<PlayerCulture> AvailableCultures { get; }

    public PlayerCulture Resolve(
        string subjectId,
        string? accountCulture = null,
        string? clientCulture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        lock (sync)
        {
            if (sessionCultures.TryGetValue(subjectId, out PlayerCulture? selected))
            {
                return selected;
            }
        }

        return TryAvailable(accountCulture, out PlayerCulture? account) ? account! :
            TryAvailable(clientCulture, out PlayerCulture? client) ? client! :
            ServerCulture;
    }

    public bool TrySetSession(string subjectId, string culture, out PlayerCulture? normalized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        if (!TryAvailable(culture, out normalized))
        {
            return false;
        }

        lock (sync)
        {
            sessionCultures[subjectId] = normalized!;
        }

        return true;
    }

    public void ResetSession(string subjectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        lock (sync)
        {
            sessionCultures.Remove(subjectId);
        }
    }

    public void RemoveSession(string subjectId) => ResetSession(subjectId);

    public string Format(MessageDescriptor message, PlayerCulture culture)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(culture);
        string? template = FindTemplate(message.Source, culture.Name, message.Key) ??
            FindTemplate(message.Source, Neutral(culture.Name), message.Key) ??
            FindTemplate(message.Source, ServerCulture.Name, message.Key) ??
            FindTemplate(message.Source, Neutral(ServerCulture.Name), message.Key);
        if (template is null)
        {
            return MessageFallbackFormatter.Format(message);
        }

        try
        {
            return string.Format(
                CultureInfo.GetCultureInfo(culture.Name),
                template,
                message.Arguments.ToArray());
        }
        catch (FormatException)
        {
            return MessageFallbackFormatter.Format(message);
        }
    }

    public IDisposable RegisterCatalog(
        string source,
        string culture,
        IReadOnlyDictionary<string, string> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(messages);
        PlayerCulture normalized = Normalize(culture);
        var copy = new Dictionary<string, string>(messages, StringComparer.Ordinal);
        if (copy.Any(static pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new ArgumentException("Localization keys and templates must not be empty.", nameof(messages));
        }

        long id = Interlocked.Increment(ref nextRegistrationId);
        var key = (source.Trim().ToLowerInvariant(), normalized.Name.ToLowerInvariant());
        lock (sync)
        {
            catalogs[key] = new CatalogRegistration(id, copy);
        }

        return new Registration(this, key, id);
    }

    private bool TryAvailable(string? value, out PlayerCulture? culture)
    {
        culture = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            PlayerCulture normalized = Normalize(value);
            culture = AvailableCultures.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            return culture is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private string? FindTemplate(string source, string culture, string key)
    {
        lock (sync)
        {
            return catalogs.TryGetValue(
                    (source.Trim().ToLowerInvariant(), culture.ToLowerInvariant()),
                    out CatalogRegistration? catalog) &&
                catalog.Messages.TryGetValue(key, out string? template)
                    ? template
                    : null;
        }
    }

    private static PlayerCulture Normalize(string culture) =>
        new(CultureInfo.GetCultureInfo(culture.Trim()).Name);

    private static string Neutral(string culture) =>
        CultureInfo.GetCultureInfo(culture).TwoLetterISOLanguageName;

    private void Unregister((string Source, string Culture) key, long id)
    {
        lock (sync)
        {
            if (catalogs.TryGetValue(key, out CatalogRegistration? current) && current.Id == id)
            {
                catalogs.Remove(key);
            }
        }
    }

    private sealed record CatalogRegistration(long Id, IReadOnlyDictionary<string, string> Messages);

    private sealed class Registration(
        LocalizationService owner,
        (string Source, string Culture) key,
        long id) : IDisposable
    {
        private LocalizationService? owner = owner;

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Unregister(key, id);
    }
}
