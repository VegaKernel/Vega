using NUnit.Framework;
using Vega.Core.GeoIP;

namespace Vega.Runtime.Tests.Core;

[TestFixture]
public sealed class IpMetadataProviderTests
{
    private string testDataDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        testDataDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "MaxMind");
    }

    [Test]
    public void CountryLookup_ReturnsKnownCountryMetadata()
    {
        using var provider = new MmdbIpMetadataProvider(
            Path.Combine(testDataDirectory, "GeoIP2-Country-Test.mmdb"),
            null);

        Assert.That(provider.TryLookup("81.2.69.160", out var metadata), Is.True);
        Assert.That(metadata.CountryCode, Is.EqualTo("GB"));
        Assert.That(metadata.Asn, Is.Null);
    }

    [Test]
    public void AsnLookup_ReturnsKnownAsnMetadata()
    {
        using var provider = new MmdbIpMetadataProvider(
            null,
            Path.Combine(testDataDirectory, "GeoLite2-ASN-Test.mmdb"));

        Assert.That(provider.TryLookup("1.128.0.0", out var metadata), Is.True);
        Assert.That(metadata.Asn, Is.EqualTo(1221));
        Assert.That(metadata.Organization, Is.EqualTo("Telstra Pty Ltd"));
        Assert.That(metadata.CountryCode, Is.Null);
    }

    [TestCase("10.10.10.10")]
    [TestCase("192.168.1.1")]
    public void PrivateAddress_ReturnsNoMetadata(string ipAddress)
    {
        using var provider = CreateFullProvider();

        Assert.That(provider.TryLookup(ipAddress, out _), Is.False);
    }

    [TestCase("")]
    [TestCase("not-an-ip")]
    [TestCase("999.999.999.999")]
    [TestCase("2001:db8::1")]
    [TestCase("::ffff:81.2.69.160")]
    public void UnsupportedOrMalformedAddress_ReturnsNoMetadata(string ipAddress)
    {
        using var provider = CreateFullProvider();

        Assert.That(provider.TryLookup(ipAddress, out _), Is.False);
    }

    [Test]
    public void WrongDatabaseType_IsRejectedAtInitialization()
    {
        var asnDatabase = Path.Combine(testDataDirectory, "GeoLite2-ASN-Test.mmdb");

        Assert.Throws<InvalidDataException>(() => new MmdbIpMetadataProvider(asnDatabase, null));
    }

    [Test]
    public void MissingDatabases_DisableProviderWithoutThrowing()
    {
        using var provider = new MmdbIpMetadataProvider(
            Path.Combine(testDataDirectory, "missing-country.mmdb"),
            Path.Combine(testDataDirectory, "missing-asn.mmdb"));

        Assert.That(provider.IsAvailable, Is.False);
        Assert.That(provider.TryLookup("81.2.69.160", out _), Is.False);
    }

    [Test]
    public void MissingCountryDatabase_DoesNotDisableAvailableAsnMetadata()
    {
        using var provider = new MmdbIpMetadataProvider(
            Path.Combine(testDataDirectory, "missing-country.mmdb"),
            Path.Combine(testDataDirectory, "GeoLite2-ASN-Test.mmdb"));

        Assert.That(provider.IsAvailable, Is.True);
        Assert.That(provider.TryLookup("1.128.0.0", out var metadata), Is.True);
        Assert.That(metadata.Asn, Is.EqualTo(1221));
        Assert.That(metadata.CountryCode, Is.Null);
    }

    private MmdbIpMetadataProvider CreateFullProvider()
    {
        return new MmdbIpMetadataProvider(
            Path.Combine(testDataDirectory, "GeoIP2-Country-Test.mmdb"),
            Path.Combine(testDataDirectory, "GeoLite2-ASN-Test.mmdb"));
    }
}
