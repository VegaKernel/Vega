# Releasing Vega

`Directory.Build.props` is the single source of truth for the Vega version. Release tags must use the matching `vMAJOR.MINOR.PATCH` form; Vega 1.0.0 is tagged `v1.0.0`.

The `Release` GitHub Actions workflow builds and tests the solution, publishes the standalone server archive, packs the public Vega components, pushes their packages to NuGet.org and creates a GitHub release with generated notes and SHA-256 checksums.

Before pushing a release tag, configure the repository Actions secret `NUGET_API_KEY` with a NuGet.org API key allowed to publish the `VegaKernel.*` package IDs. Then create and push the tag:

```powershell
git tag -a v1.0.0 -m "Vega 1.0.0"
git push origin v1.0.0
```

The workflow rejects malformed tags and tags whose version does not match `VersionPrefix`. A GitHub release is created only after every NuGet package has been accepted or already exists with the same version.
