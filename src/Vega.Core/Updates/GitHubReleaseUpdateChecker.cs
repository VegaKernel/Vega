using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Vega.Contracts.Updates;

namespace Vega.Core.Updates;

public sealed class GitHubReleaseUpdateChecker : IUpdateChecker, IDisposable
{
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;
    private readonly string currentVersion;

    public GitHubReleaseUpdateChecker(HttpClient? httpClient = null, string? currentVersion = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        ownsClient = httpClient is null;
        this.currentVersion = string.IsNullOrWhiteSpace(currentVersion)
            ? GetCurrentVersion()
            : currentVersion.Trim();
        this.httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Vega", "1.0"));
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            "https://api.github.com/repos/VegaKernel/Vega/releases/latest",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new UpdateCheckResult(currentVersion, null, false);
        }

        response.EnsureSuccessStatusCode();

        await using Stream content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("tag_name", out JsonElement tag) ||
            string.IsNullOrWhiteSpace(tag.GetString()))
        {
            throw new InvalidDataException("The Vega release response did not contain a tag name.");
        }

        string latestVersion = tag.GetString()!.Trim();
        Version current = ParseVersion(currentVersion, "running Vega version");
        Version latest = ParseVersion(latestVersion, "Vega release tag");
        return new UpdateCheckResult(currentVersion, latestVersion, latest > current);
    }

    public void Dispose()
    {
        if (ownsClient) httpClient.Dispose();
    }

    private static string GetCurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? typeof(GitHubReleaseUpdateChecker).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static Version ParseVersion(string value, string source)
    {
        string normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        int suffix = normalized.IndexOfAny(['-', '+']);
        if (suffix >= 0)
        {
            normalized = normalized[..suffix];
        }

        if (!Version.TryParse(normalized, out Version? parsed))
        {
            throw new InvalidDataException($"The {source} '{value}' is not a supported semantic version.");
        }

        return new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));
    }
}
