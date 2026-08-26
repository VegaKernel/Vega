using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Vega.Core.GeoIP;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class GeoIpDatabaseUpdaterTests
{
    [Test]
    public async Task MatchingGitBlobHashes_DoNotDownloadDatabases()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] payload = [1, 2, 3, 4, 5];
            string[] files = ["GeoLite2-ASN.mmdb", "GeoLite2-City.mmdb", "GeoLite2-Country.mmdb"];
            foreach (string file in files)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, file), payload);
            }

            string sha = await GeoIpDatabaseUpdater.ComputeGitBlobShaAsync(
                Path.Combine(directory, files[0]));
            int requests = 0;
            using var client = new HttpClient(new DelegateHandler(request =>
            {
                requests++;
                Assert.That(request.RequestUri!.Host, Is.EqualTo("api.github.com"));
                return TreeResponse(files.Select(file => (file, sha, (long)payload.Length)));
            }));
            using var updater = new GeoIpDatabaseUpdater(directory, false, client);

            await updater.CheckForUpdatesAsync();

            Assert.That(requests, Is.EqualTo(1));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task HashMismatch_KeepsPreviousDatabase()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] unchanged = [1, 2, 3];
            byte[] previousCountry = [4, 5, 6];
            byte[] expectedCountry = [7, 8, 9];
            byte[] corruptDownload = [9, 8, 7];

            string asnPath = Path.Combine(directory, "GeoLite2-ASN.mmdb");
            string cityPath = Path.Combine(directory, "GeoLite2-City.mmdb");
            string countryPath = Path.Combine(directory, "GeoLite2-Country.mmdb");
            await File.WriteAllBytesAsync(asnPath, unchanged);
            await File.WriteAllBytesAsync(cityPath, unchanged);
            await File.WriteAllBytesAsync(countryPath, previousCountry);

            string unchangedSha = await GeoIpDatabaseUpdater.ComputeGitBlobShaAsync(asnPath);
            string expectedPath = Path.Combine(directory, "expected.bin");
            await File.WriteAllBytesAsync(expectedPath, expectedCountry);
            string expectedCountrySha = await GeoIpDatabaseUpdater.ComputeGitBlobShaAsync(expectedPath);
            File.Delete(expectedPath);

            int rawRequests = 0;
            using var client = new HttpClient(new DelegateHandler(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                {
                    return TreeResponse(
                    [
                        ("GeoLite2-ASN.mmdb", unchangedSha, (long)unchanged.Length),
                        ("GeoLite2-City.mmdb", unchangedSha, (long)unchanged.Length),
                        ("GeoLite2-Country.mmdb", expectedCountrySha, (long)expectedCountry.Length)
                    ]);
                }

                rawRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(corruptDownload)
                };
            }));
            using var updater = new GeoIpDatabaseUpdater(directory, false, client);

            await updater.CheckForUpdatesAsync();

            Assert.That(rawRequests, Is.EqualTo(1));
            Assert.That(await File.ReadAllBytesAsync(countryPath), Is.EqualTo(previousCountry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ValidHashButInvalidMmdb_KeepsPreviousDatabase()
    {
        string directory = CreateTempDirectory();
        try
        {
            byte[] unchanged = [1, 2, 3];
            byte[] previousCountry = [4, 5, 6];
            byte[] invalidMmdb = Encoding.UTF8.GetBytes("not-a-maxmind-database");

            string asnPath = Path.Combine(directory, "GeoLite2-ASN.mmdb");
            string cityPath = Path.Combine(directory, "GeoLite2-City.mmdb");
            string countryPath = Path.Combine(directory, "GeoLite2-Country.mmdb");
            string candidatePath = Path.Combine(directory, "candidate.bin");
            await File.WriteAllBytesAsync(asnPath, unchanged);
            await File.WriteAllBytesAsync(cityPath, unchanged);
            await File.WriteAllBytesAsync(countryPath, previousCountry);
            await File.WriteAllBytesAsync(candidatePath, invalidMmdb);

            string unchangedSha = await GeoIpDatabaseUpdater.ComputeGitBlobShaAsync(asnPath);
            string candidateSha = await GeoIpDatabaseUpdater.ComputeGitBlobShaAsync(candidatePath);
            File.Delete(candidatePath);

            using var client = new HttpClient(new DelegateHandler(request =>
            {
                if (request.RequestUri!.Host == "api.github.com")
                {
                    return TreeResponse(
                    [
                        ("GeoLite2-ASN.mmdb", unchangedSha, (long)unchanged.Length),
                        ("GeoLite2-City.mmdb", unchangedSha, (long)unchanged.Length),
                        ("GeoLite2-Country.mmdb", candidateSha, (long)invalidMmdb.Length)
                    ]);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(invalidMmdb)
                };
            }));
            using var updater = new GeoIpDatabaseUpdater(directory, false, client);

            await updater.CheckForUpdatesAsync();

            Assert.That(await File.ReadAllBytesAsync(countryPath), Is.EqualTo(previousCountry));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void AutoUpdateOption_IsReadFromServerConfiguration()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "server.json");
            File.WriteAllText(path, "{\"GeoIP\":{\"AutoUpdate\":true}}");

            GeoIpAutoUpdateOptions options = GeoIpAutoUpdateOptions.Load(path);

            Assert.That(options.AutoUpdate, Is.True);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpResponseMessage TreeResponse(
        IEnumerable<(string Path, string Sha, long Size)> entries)
    {
        string json = JsonSerializer.Serialize(new
        {
            truncated = false,
            tree = entries.Select(entry => new
            {
                path = entry.Path,
                type = "blob",
                sha = entry.Sha,
                size = entry.Size
            })
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vega-geoip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
