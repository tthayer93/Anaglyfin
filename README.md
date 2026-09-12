# Anaglyfin

Jellyfin plugin that exposes 3D MVC sources as selectable playback versions
(2D base, red-cyan anaglyph, full SBS, half SBS), played back through the normal
Jellyfin HLS pipeline.

Design and plan live in `PROJECT_CONTEXT.md`, `STATUS_SUMMARY.md` and `PLAN.md`.

## Current state

Bootstrap scaffold only. The plugin builds against Jellyfin 12 and does nothing
yet: no profiles, no media sources, no FFmpeg wrapper, no settings UI, no caching.

## Requirements

| Item | Value |
| --- | --- |
| .NET SDK | `10.0.x` (pinned by `global.json`, `rollForward: latestMinor`) |
| Target framework | `net10.0` |
| Jellyfin ABI | `Jellyfin.Controller` / `Jellyfin.Model` `12.0.0` |

## Repository layout

```text
.ci/                     CI container definition (toolchain only)
src/Anaglyfin/           Plugin project (entry point, service registrator, manifest)
tests/Anaglyfin.Tests/   xUnit unit tests
Anaglyfin.sln            Solution: plugin + tests
```

* `Plugin` derives from `BasePlugin<PluginConfiguration>` and carries the fixed
  plugin GUID (`Plugin.PluginId`). The same GUID is declared in
  `src/Anaglyfin/Plugin.manifest.xml`, which is embedded in the assembly;
  `PluginManifestTests` fails if the two ever disagree.
* `PluginServiceRegistrator` is the DI seam Jellyfin discovers by assembly scan.
  It registers nothing yet and must keep a public parameterless constructor.

## CI

The whole gate runs in a container; the only parameter is `SRC_DIR`, which is used
both as the Docker build context and as the source bind mount:

```sh
SRC_DIR="$HOST_WORKSPACE/Anaglyfin" docker compose -f .ci/test.yml run --rm test
```

Steps executed by the job: `dotnet restore` → `dotnet build -warnaserror` (Release)
→ `dotnet test` → `dotnet format whitespace --verify-no-changes`.

Job conventions: scratch space `/tmp/anaglyfin-test`, test port `8098` reserved
for a future integration job (nothing listens yet, so it is not published), and
no database. The job runs on the host network namespace: it listens on nothing
and talks to no other service, so it does not need (and does not consume) a
daemon-managed bridge network.

The container runs as UID/GID `1000:1000` (overridable with the `BUILD_UID` /
`BUILD_GID` build args) so build output written into the bind mount is not
root-owned, with `HOME`/`DOTNET_CLI_HOME`/`NUGET_PACKAGES` pointed at `/tmp` so
restore has writable state.

`BUILD_CONTEXT` may be set when the compose CLI and the Docker daemon see the
same checkout under different mount points; it overrides only the build context
and must never be needed on a machine where `$HOST_WORKSPACE` is readable by
both.

Style enforcement is `dotnet format whitespace` (formatter/`.editorconfig`
conformance) plus compiler warnings as errors. StyleCop and the Jellyfin
analyzer set are deliberately not wired in yet; adding them is a
dependency-level change, not an in-task one.
