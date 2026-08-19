# TShockNG

[Русский](README.md) | [English](README_EN.md)

TShockNG is a modernized Terraria server platform based on TShock and Terraria Server API. The project keeps familiar TShock concepts where plugins still need them while progressively moving behavior behind explicit contracts and services, without requiring binary compatibility with previously compiled plugins.

> The project is undergoing active architectural modernization. Accepted decisions and migration order are documented in [`docs/decisions`](docs/decisions) and [`docs/roadmap.md`](docs/roadmap.md).

## Direction

- .NET 10 as the single target platform.
- `TShockNG.Contracts` for implementation-independent contracts, DTOs, and interfaces.
- `TShockNG.Core` as the target home of server behavior and application services.
- `TShockAPI` as a transitional integration layer and source-level surface for existing hooks and commands.
- Typed Terraria protocol handling through `TZ.Multiplicity`.
- Explicit separation of game-thread state, background work, infrastructure, and domain logic.
- Incremental dismantling of legacy monoliths without preserving binary compatibility solely for already compiled plugins.

## Repository layout

```text
src/
  TShockAPI/            transitional TShock/API integration
  TShockNG.Contracts/   implementation-independent contracts
  TShockNG.Core/        modern server behavior
  TShockLauncher/       server launcher
  TShockInstaller/      installer
  TShockPluginManager/  plugin management

tests/                  tests
external/
  TSAPI/                 Terraria Server API git submodule
docs/                    architecture, ADRs, and documentation
i18n/                    localizations
```

## Requirements

- .NET SDK 10 for builds
- ASP.NET Core Runtime 10 for framework-dependent deployments
- Git with submodule support

## Clone

```bash
git clone --recurse-submodules https://github.com/TShockNG/TShockNG.git
cd TShockNG
```

If the repository was cloned without submodules:

```bash
git submodule update --init --recursive
```

## Build

```bash
dotnet restore TShock.sln
dotnet build TShock.sln
```

## Tests

```bash
dotnet test TShock.sln
```

CI also builds and tests the linked TSAPI repository.

## Run

```bash
dotnet run --project src/TShockLauncher
```

## Publish

Example framework-dependent Linux x64 build:

```bash
dotnet publish src/TShockLauncher/TShockLauncher.csproj \
  -c Release \
  -f net10.0 \
  -r linux-x64 \
  --self-contained false
```

The Runtime Identifier can be replaced with `win-x64`, `linux-arm64`, or another .NET 10 RID supported by the project.

## GeoIP

GeoIP uses local MaxMind MMDB databases. When GeoIP is enabled, the server looks for these files under the TShock save path:

```text
GeoLite2-Country.mmdb
GeoLite2-ASN.mmdb
```

Databases are never downloaded on the player connection path. Country/ASN metadata remains separate from future VPN/proxy/Tor reputation policy.

## Plugin compatibility

TShockNG **does not guarantee binary compatibility** with previously compiled TShock plugins. Plugins may need to be rebuilt.

During migration, a narrow source-level surface remains available for registering existing hooks and `Command` objects. New subsystems should use TShockNG contracts and services instead of expanding legacy global APIs.

See [`ADR 0015`](docs/decisions/0015-source-level-plugin-registration-contracts.md).

## Architecture

Primary documentation:

- [`docs/architecture.md`](docs/architecture.md) — current architecture;
- [`docs/roadmap.md`](docs/roadmap.md) — modernization roadmap;
- [`docs/decisions`](docs/decisions) — architecture decision records.

## License

TShockNG is distributed under GPL-3.0-or-later. See [`COPYING`](COPYING) for the full license text.
