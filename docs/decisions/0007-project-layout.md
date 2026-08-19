# ADR 0007: Normalize repository layout after the .NET 10 baseline

Status: Accepted

## Context

The repository root currently mixes product source projects, tests, an external TSAPI submodule, documentation, build files, and repository metadata. That makes navigation and future architectural work noisier than necessary.

The active .NET 10 migration must reach a green, warning-free baseline before paths are moved. Mixing framework/package changes with a repository-wide relocation would make failures harder to attribute and review.

## Decision

After the .NET 10 baseline is merged and verified, normalize the repository layout as a separate mechanical change.

Target shape:

```text
.github/
docs/
external/
  TSAPI/
src/
  TShockAPI/
  TShockInstaller/
  TShockLauncher/
  TShockPluginManager/
tests/
  TShockLauncher.Tests/
prebuilts/
README.md
COPYING
.gitignore
.gitmodules
TShock.sln
Dockerfile
```

`TSAPI` remains a separate repository/submodule and therefore belongs under `external/`, not under `src/`.

Repository-level files required by GitHub, Git, Docker, licensing, solution discovery, and contributors remain at the root. Documentation remains under `docs/`.

Repository-wide assets and tooling that are not first-party product projects, including `i18n/`, `scripts/`, editor metadata, and CI/service configuration, remain at the root. The normalized boundary in this ADR separates first-party source, tests, and external repositories without relocating unrelated repository-wide assets.

## Migration requirements

- Perform the move only after the .NET 10 baseline is green.
- Preserve package identities and namespaces only where they remain useful; repository relocation does not carry a binary-compatibility requirement.
- Update all project references, solution paths, workflow paths, Docker paths, scripts, documentation links, and `.gitmodules` atomically.
- Keep build/test/pack green with zero warnings after the move.
- Do not combine the layout change with behavior changes.

## Consequences

The root becomes substantially easier to scan and future modernization can distinguish first-party source, tests, external dependencies, and documentation. The relocation will create a large mechanical diff, which is why it is deliberately deferred until after the runtime/dependency baseline is stable.
