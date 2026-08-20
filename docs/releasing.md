# Releasing Vega

`Directory.Build.props` is the single source of truth for the Vega version. Release tags must use the matching `vMAJOR.MINOR.PATCH` form; Vega 1.0.0 is tagged `v1.0.0`.

The manual `Release` GitHub Actions workflow selects the newest `vMAJOR.MINOR.PATCH` tag, builds and tests that exact revision, publishes the standalone server archive, packs the public Vega components, pushes their packages to NuGet.org and creates or refreshes a GitHub release with generated notes and SHA-256 checksums.

The server archive is ready to start after extraction. Runtime libraries live in `libs/`; first-party server modules live in `ServerPlugins/`. `Vega.Server.exe` and its host metadata stay at the archive root.

Before running the workflow, configure the repository Actions secret `NUGET_API_KEY` with a NuGet.org API key allowed to publish the `VegaKernel.*` package IDs. Create and push the tag, then run the `Release` workflow from the Actions page:

```powershell
git tag -a v1.0.0 -m "Vega 1.0.0"
git push origin v1.0.0
```

The workflow rejects malformed tags and tags whose version does not match `VersionPrefix`. A GitHub release is created only after every NuGet package has been accepted or already exists with the same version. Re-running the workflow is safe: NuGet duplicates are skipped and existing release assets are replaced.
