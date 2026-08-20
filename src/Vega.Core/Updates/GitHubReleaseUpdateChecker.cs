using System.Net.Http.Headers;
using System.Text.Json;
using Vega.Contracts.Updates;

namespace Vega.Core.Updates;

public sealed class GitHubReleaseUpdateChecker : IUpdateChecker, IDisposable
{
	private readonly HttpClient httpClient;
	private readonly bool ownsClient;

	public GitHubReleaseUpdateChecker(HttpClient? httpClient = null)
	{
		this.httpClient = httpClient ?? new HttpClient();
		ownsClient = httpClient is null;
		this.httpClient.DefaultRequestHeaders.UserAgent.Add(
			new ProductInfoHeaderValue("Vega", "1.0"));
	}

	public async Task CheckAsync(CancellationToken cancellationToken = default)
	{
		using HttpResponseMessage response = await httpClient.GetAsync(
			"https://api.github.com/repos/VegaKernel/Vega/releases/latest",
			HttpCompletionOption.ResponseHeadersRead,
			cancellationToken).ConfigureAwait(false);
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
	}

	public void Dispose()
	{
		if (ownsClient) httpClient.Dispose();
	}
}
