# Releasing Vega

`Directory.Build.props` is the single source of truth for the Vega base version. While active development uses a preview .NET runtime, new release tags must add a SemVer prerelease suffix, for example `v1.0.1-preview.1` for `VersionPrefix` 1.0.1. Stable `vMAJOR.MINOR.PATCH` tags remain reserved for the production runtime channel.

Vega has two release workflows with separate responsibilities.

## Automatic tagged release

`Release Tag` runs when a `v*` tag is pushed. During the .NET 11 preview baseline it requires `vMAJOR.MINOR.PATCH-SUFFIX`, requires the base version to match `VersionPrefix`, and marks the GitHub release as a prerelease. The workflow restores, builds and tests the tagged revision, creates the single `VegaKernel` NuGet package, publishes it to NuGet.org, and creates or refreshes the matching GitHub Release.

Configure the repository Actions secret `NUGET_API_KEY` with a NuGet.org API key allowed to publish the `VegaKernel` package ID before pushing the tag:

```powershell
git tag -a v1.0.1-preview.1 -m "Vega 1.0.1 preview 1"
git push origin v1.0.1-preview.1
```

The GitHub Release contains the deployable server ZIP, the single `VegaKernel.*.nupkg`, and `SHA256SUMS.txt`. NuGet duplicates are skipped and existing GitHub Release assets are replaced, so a previously interrupted run can be re-run safely.

## Manual server publication

`Publish Manual` is started from the Actions page. It selects and checks out the newest stable `vMAJOR.MINOR.PATCH` tag, verifies that the tag matches `VersionPrefix`, selects that tag's pinned SDK (falling back to .NET 10 for pre-`global.json` tags), restores and publishes only `Vega.Runtime`, and creates or refreshes the matching GitHub Release. It deliberately skips tests, package creation and NuGet publication.

Use this workflow when NuGet packages are already published and only the server distribution needs to be regenerated. It does not require `NUGET_API_KEY`.

## Server archive layout

Both workflows publish `Vega.Server-VERSION.zip`. The archive is ready to start immediately after extraction: `Vega.Server.exe`, host metadata, the license, both README files and `VERSION` are at its root; runtime libraries are under `libs/`; bundled first-party modules are under `ServerPlugins/`. The legacy `bin/` and `Plugins/` directories are rejected during publication.
