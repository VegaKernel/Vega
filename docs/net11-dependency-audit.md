# .NET 11 dependency audit

This audit records the dependency state of the .NET 11 Preview 7 development baseline on 2026-08-24. It covers every direct package and the 45 third-party entries in the resolved `Vega.Runtime` graph. A successful NuGet resolution means an older compatible asset was selected; it is not a claim that the package vendor supports .NET 11.

## Direct dependencies and selected assets

| Area | Resolved packages | Selected assets | Decision |
| --- | --- | --- | --- |
| Terraria and hooks | TerrariaRuntime 1.0.1, Multiplicity 2.6.2 | `net11.0` plus Multiplicity's compatible asset | Use the single current TerrariaRuntime package. ModFramework and the legacy Upcoming package are no longer part of the graph. Production promotion still requires real hook, world and network validation. |
| Runtime detours | MonoMod.Core 1.3.6, RuntimeDetour 25.3.6, Utils 25.0.14, Backports 1.1.2 and ILHelpers 1.1.0 | `net5.0` through `net9.0`; patched Core is supplied by TerrariaRuntime | The resolved stack does not officially accept `net11.0`. Vega keeps the explicit opt-in and its warning; this is the outstanding compatibility exception. |
| Persistence | Microsoft.Data.Sqlite 11.0.0-preview.7.26381.103, MySqlConnector 2.6.2, Npgsql 10.0.3, SQLitePCLRaw 2.1.13 | `net10.0`, `net6.0` and `netstandard2.0` | Align Sqlite with the pinned SDK preview. Local SQLite tests pass; PostgreSQL/MySQL remain covered by their service-backed CI job and are not represented by skipped local tests. |
| Runtime system assemblies | System.Configuration.ConfigurationManager and System.Drawing.Common 11.0.0-preview.7.26381.103 | `net11.0` | Align with the pinned runtime because TerrariaRuntime exposes types that require these undeclared assemblies. |
| Operations UI | Terminal.Gui 2.4.17 and Terminal.Gui.Editor 2.5.7 | `net10.0` | Retain the latest stable releases; newer entries are development prereleases. |
| GeoIP | MaxMind.GeoIP2 6.1.0 and MaxMind.Db 5.1.0 | `net10.0` | Retain; fixture-backed country and reload tests pass. |
| Tests | Microsoft.NET.Test.Sdk 18.9.0, NUnit 4.6.1, NUnit3TestAdapter 6.2.0, NUnit.Analyzers 4.14.0 and coverlet.collector 10.0.1 | supported runner/analyzer assets | Upgrade Test SDK to the current stable version. Retain the current stable adapter, framework, analyzer and collector versions. |

The remaining transitive graph consists of the Microsoft.Extensions.Configuration 10.0.7 family; System.IO.Abstractions 22.1.1 and TestableIO wrappers; Newtonsoft.Json 13.0.4; Markdig 1.3.2; TextMateSharp 2.0.4 and grammars; ColorHelper, Onigwrap and Wcwidth; Steamworks.NET 2024.8.0; Mono.Cecil 0.11.6; JetBrains.Annotations 2026.2.0; and framework crypto, packaging, event-log and protected-data packages. Their selected assets range from `netstandard2.0` to `net10.0`. Restore, compilation, tests, clean package consumption and the Windows deployment smoke found no loader, API or native-asset regression in those paths.

## Changes made by the audit

- TerrariaRuntime 1.0.1 replaced the legacy Upcoming and ModFramework packages. Vega now consumes native `net11.0` runtime and hook assemblies from one package.
- Microsoft.Data.Sqlite, System.Configuration.ConfigurationManager and System.Drawing.Common moved from .NET 10 packages to the pinned .NET 11 Preview 7 package version. No schema, configuration or runtime-layout change was required.
- Microsoft.NET.Test.Sdk moved from 18.8.1 to 18.9.0. The test count and execution model remain unchanged, except for the new compatibility guards added with this audit.
- Every bundled `Vega.Modules.*` project is guarded as `net11.0`. The hot-plugin loader reads `TargetFrameworkAttribute` before loading the assembly and rejects a newer or different runtime with an actionable startup diagnostic.
- The unified `Vega` package carries the repository's explicit MonoMod opt-in in `buildTransitive/Vega.props`, so a clean `net11.0` consumer receives the same visible unsupported-runtime warning instead of an unexplained build failure.

The Vega runtime maintainers own that package-level MonoMod opt-in. Remove it as soon as the resolved TerrariaRuntime/MonoMod dependency graph officially accepts `net11.0`; until then, the warning and the unchecked production smoke/soak gates must remain visible. TerrariaRuntime owns the required MonoMod.Core patch and copies that assembly through its build-transitive target; Vega does not carry a binding redirect or a second runtime fork.

## Validation evidence

- pinned-SDK restore and Release build: zero errors; the three known MonoMod unsupported-runtime warnings remain;
- architecture suite: 29 passed;
- runtime suite: 425 passed, with only the two service-backed PostgreSQL/MySQL cases skipped locally;
- hot-plugin lifecycle and incompatible-TFM diagnostic tests: 4 passed;
- the single public `Vega` package inspected for all 18 `net11.0` first-party DLL/PDB pairs, metadata files and leaked build directories;
- `Vega` restored in a clean `net11.0` class-library consumer with a fresh global-packages directory, then built successfully;
- clean Windows deployable layout reached the scripted startup smoke checkpoint.

These results close the repository dependency audit, tooling refresh, bundled-module TFM and clean package-consumer tasks. They do not close official MonoMod support, external-provider execution, every supported OS/mode, representative production-world operation, performance comparison or deployment-host soak.
