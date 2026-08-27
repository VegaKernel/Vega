# ADR 0008: Replace legacy GeoIP with an MMDB-based IP metadata provider

Status: Accepted

## Context

TShockNG still contains a legacy MaxMind GeoIP parser and database layout. That code was kept compiling during the .NET 10 baseline only so framework migration, dependency cleanup, and behavior changes remained separable.

GeoIP metadata and proxy/reputation detection are different concerns. Country/ASN lookup should not be coupled to VPN, proxy, Tor, hosting, or DNSBL policy.

## Decision

Replace the legacy GeoIP database implementation with a modern local MMDB-based provider.

The provider contract is exposed through `TShockNG.Contracts.Networking.IIpMetadataProvider` and `IpMetadata`, while the MMDB implementation lives in `TShockNG.Core`:

```text
Country
CountryCode
Continent
ASN
Organization
```

Use local GeoIP2/GeoLite2 Country and ASN MMDB databases through `MaxMind.GeoIP2`. `DatabaseReader` instances are opened once and reused for connection-time lookups; there is no runtime HTTP lookup or database download on player connection.

Vega accepts explicit country and ASN MMDB paths through its IP metadata provider contract. Missing databases disable only the unavailable metadata source; if neither database exists, GeoIP remains inactive. Invalid existing databases fail closed at provider initialization and do not block server startup.

The runtime may optionally maintain local GeoLite2 ASN, City and Country databases in the background when `GeoIP.AutoUpdate` is enabled in `config/server.json`. The updater reads Git tree metadata from the `download` branch of `P3TERX/GeoLite.mmdb`, compares the remote Git blob SHA-1 with the local file before downloading, validates both the downloaded hash and MaxMind database type, and atomically replaces only a validated database. A failed HTTP request, hash check or MMDB validation leaves the previous local database untouched. Update work is cancellable and stays entirely outside player admission and lookup paths.

The old database file, parser and compatibility adapter are removed from the repository and distribution. The old database format is not supported.

The MMDB provider does not expose the historical proxy pseudo-country, so reputation policy is no longer inferred from geolocation metadata; a later reputation ADR may introduce an independent policy contract.

## Separation from anti-proxy

Anti-proxy and IP reputation are explicitly deferred to a later, separate stage.

A future reputation subsystem may expose signals such as:

```text
VPN
Proxy
Tor
ResidentialProxy
HostingProvider
Datacenter
DNSBL indicators
```

Those signals must live behind a separate reputation contract and policy layer. DNSBL results must not be treated as a direct replacement for GeoIP metadata and should not by themselves imply an automatic kick or ban.

## Migration requirements

- Do not perform this replacement before the .NET 10 baseline is green and merged.
- Preserve join country display behavior when country metadata is available; binary/config compatibility is not a requirement.
- Keep lookup local and fast on the connection path.
- Do not introduce mandatory external HTTP dependencies for player login.
- Keep database update/download logic outside the hot connection path.
- Keep geolocation/ASN metadata independent from future anti-proxy policy.
- Add focused tests for known addresses, unknown/private addresses, malformed input, and missing/unavailable MMDB databases.

## Validation

Automated tests cover country metadata, ASN/organization metadata, private/unknown addresses, malformed input and missing MMDB files. CI downloads the small official MaxMind test databases from a pinned upstream commit solely as test fixtures. Updater tests additionally cover no-download behavior for matching Git blob hashes, preservation of the previous database on hash mismatch, and rejection of a hash-valid but malformed MMDB payload.

## Consequences

TShockNG stops carrying a legacy database parser and gains a replaceable, testable IP metadata abstraction. Production Vega may make optional background HTTP requests for database maintenance, but server startup, player login and connection-time metadata lookup never depend on those requests succeeding. Anti-proxy can then evolve independently without turning geolocation into a security-policy grab bag.