# Releasing Vega

`Directory.Build.props` is the single source of truth for the Vega version. Release tags must use the matching `vMAJOR.MINOR.PATCH` form; Vega 1.0.0 uses `v1.0.0`.

Vega has two release workflows with separate responsibilities.

## Automatic tagged release

`Release Tag` runs when a `v*` tag is pushed. It accepts only the exact `vMAJOR.MINOR.PATCH` form and requires the tag version to match `VersionPrefix`. The workflow restores, builds and tests the tagged revision, packs the public `VegaKernel.*` libraries and symbol packages, publishes them to NuGet.org, and creates or refreshes the matching GitHub Release.

Configure the repository Actions secret `NUGET_API_KEY` with a NuGet.org API key allowed to publish the `VegaKernel.*` package IDs before pushing the tag:

```powershell
git tag -a v1.0.0 -m "Vega 1.0.0"
git push origin v1.0.0
```

The GitHub Release contains the deployable server ZIP, all `.nupkg` and `.snupkg` files, and `SHA256SUMS.txt`. NuGet duplicates are skipped and existing GitHub Release assets are replaced, so a previously interrupted run can be re-run safely.

## Manual server publication

`Publish Manual` is started from the Actions page. It selects and checks out the newest `vMAJOR.MINOR.PATCH` tag, verifies that the tag matches `VersionPrefix`, restores and publishes only `Vega.Runtime`, and creates or refreshes the matching GitHub Release. It deliberately skips tests, package creation and NuGet publication.

Use this workflow when NuGet packages are already published and only the server distribution needs to be regenerated. It does not require `NUGET_API_KEY`.

## Server archive layout

Both workflows publish `Vega.Server-VERSION.zip`. The archive is ready to start immediately after extraction: `Vega.Server.exe`, host metadata, the license, both README files and `VERSION` are at its root; runtime libraries are under `libs/`; bundled first-party modules are under `ServerPlugins/`. The legacy `bin/` and `Plugins/` directories are rejected during publication.
