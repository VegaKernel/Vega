# ADR 0008: Replace legacy GeoIP with an MMDB-based IP metadata provider

Status: Planned

## Context

TShockNG still contains a legacy `GeoIPCountry` implementation derived from MaxMind's old GeoIP format and a `GeoIP.dat` database layout. That code is kept compiling during the .NET 10 baseline only so framework migration, dependency cleanup, and behavior changes remain separable.

GeoIP metadata and proxy/reputation detection are different concerns. Country/ASN lookup should not be coupled to VPN, proxy, Tor, hosting, or DNSBL policy.

## Decision

After the .NET 10 baseline is merged and verified, replace the legacy GeoIP implementation with a modern local MMDB-based provider.

The planned provider should expose IP metadata through a small service contract, for example:

```text
Country
CountryCode
Continent
ASN
Organization
```

Use local MMDB databases such as GeoLite2 Country and GeoLite2 ASN rather than performing an HTTP request for every player connection. Database refresh must be independent from connection-time lookup.

The legacy `GeoIPCountry.cs` implementation and support for the old `GeoIP.dat` format should be removed once the replacement is integrated and compatibility requirements are satisfied.

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
- Preserve existing externally visible behavior where compatibility is required.
- Keep lookup local and fast on the connection path.
- Do not introduce mandatory external HTTP dependencies for player login.
- Keep database update/download logic outside the hot connection path.
- Keep geolocation/ASN metadata independent from future anti-proxy policy.
- Add focused tests for known addresses, unknown/private addresses, malformed input, and missing/unavailable MMDB databases.

## Consequences

TShockNG stops carrying a legacy database parser and gains a replaceable, testable IP metadata abstraction. Anti-proxy can then evolve independently without turning geolocation into a security-policy grab bag.
