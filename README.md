# Anaglyfin

Jellyfin plugin that exposes 3D MVC sources as selectable playback versions
(2D base, red-cyan anaglyph, full SBS, half SBS), played back through the normal
Jellyfin HLS pipeline.

Runtime design and validation notes live in `docs/architecture.md` and
`docs/validation.md`. Install and packaging layout - bare-metal and Docker - is in
`docs/install.md`. Orchestrator planning notes are not tracked in this checkout.

## Current state

The source tree now contains the working code path, not just the bootstrap scaffold:

- fixed plugin identity, manifest, and service registration
- profile catalog with the shipped 2D, SBS, anaglyph, and custom grayscale profiles
- plugin configuration model and dashboard admin page
- conservative MVC detection rules
- alternate media source provider that adds profile-marked playback versions
- default profiles applied per exact registered device, read from the requesting client
- profile marker and parser contract
- exact FFmpeg profile argument builder
- out-of-process `Anaglyfin.FFmpegWrapper` executable with marker rewrite,
  concurrency guard, and real FFmpeg launcher
- `tools/Anaglyfin.Packager`, which writes the plugin zip, `meta.json`, and the staged
  linux-x64 wrapper, and refuses a build whose assembly and manifest disagree
- xUnit tests covering the pure seams, the command contract, and the packaging job

Real Jellyfin runtime validation is tracked separately in `docs/validation.md`, and the install
shapes for a bare-metal server and for `jellyfin/jellyfin:latest` are in `docs/install.md`.
Before either is visited for real, `dev/jellyfin-validation/` runs the packaged plugin and
wrapper against a disposable `jellyfin/jellyfin:latest` mounted the way the target server is.

Device defaults are applied, not merely stored. The provider reads the exact
`Jellyfin-DeviceId` claim of the authenticated request it is answering, so a default pinned to
one registered device is offered first to that device and to no other client; an API-key call,
or a provider call with no HTTP context behind it at all, falls back to the global default. A
default only chooses which version is offered first - it authorises no playback and changes no
library item - and because a device id is client-generated and can be regenerated, it is a
convenience key rather than a security boundary. What that ordering looks like on a real
registered device is still an open entry of `docs/validation.md` (V5.2), not a result recorded
here.

The remaining implementation follow-ups are documented in those files and include subtitle
ordinal wiring through the provider. Selecting by approximate device category - TV, phone,
tablet, VR headset, 3D-capable projector - is deferred future work and not a current capability:
Jellyfin 12 has no reliable native server-side device type to key one on, so such a feature can
only ever be an explicitly labelled heuristic over what clients report about themselves.

## Requirements

| Item | Value |
| --- | --- |
| .NET SDK | `10.0.x` (pinned by `global.json`, `rollForward: latestMinor`) |
| Target framework | `net10.0` |
| Jellyfin ABI | `Jellyfin.Controller` / `Jellyfin.Model` `12.0.0` |
| FFmpeg runtime | FFmpeg-mvc `jellyfin-8.1` build, reached through the wrapper |

## Repository layout

```text
.ci/                           CI container definition (toolchain only)
dev/jellyfin-validation/       Disposable Jellyfin 12 stack for runtime validation
docs/                          Architecture, install, and validation notes
src/Anaglyfin/                 Plugin project
src/Anaglyfin.FFmpegWrapper/   Out-of-process FFmpeg wrapper executable
tools/Anaglyfin.Packager/      Build-time packaging job for the plugin zip and wrapper artifact
tests/Anaglyfin.Tests/         xUnit unit tests
Anaglyfin.sln                  Solution: plugin + wrapper + packager + tests
```

Packaging output goes to `artifacts/` and publish output under `bin/`; both are ignored.
Runtime-validation state - a throwaway Jellyfin config, cache, and media mount - goes under
`dev/jellyfin-validation/`, and is ignored too.

* `Plugin` derives from `BasePlugin<PluginConfiguration>` and carries the fixed
  plugin GUID (`Plugin.PluginId`). The same GUID is declared in
  `src/Anaglyfin/Plugin.manifest.xml`, which is embedded in the assembly;
  `PluginManifestTests` fails if the two ever disagree.
* `PluginServiceRegistrator` is the DI seam Jellyfin discovers by assembly scan.
  It registers the profile catalog, MVC detector, and settings source, and must
  keep a public parameterless constructor.
* `AnaglyfinMediaSourceProvider` is discovered by Jellyfin's media-source provider
  scan. It must not be registered manually in DI.
* `Anaglyfin.FFmpegWrapper` is a normal executable, not a plugin. In a wrapper-based
  deployment, the server's FFmpeg path points at it and it resolves the real
  FFmpeg-mvc binary through environment variables.

## Wrapper environment

The wrapper is started by Jellyfin, not by the plugin, so deployment settings come from
the server environment:

```text
ANAGLYFIN_REAL_FFMPEG
FFMPEG_MVC_PATH
ANAGLYFIN_LOCK_DIR
ANAGLYFIN_WRAPPER_SETTINGS
```

The admin page owns the subtitle-depth request and the concurrency limit. The plugin publishes both
to the settings document named by `ANAGLYFIN_WRAPPER_SETTINGS`. Optional
`ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` overrides the page's concurrency limit for wrapper processes
that receive that environment.

Real deployment details and expected validation results are in
`docs/validation.md`.

## CI

The whole gate runs in a container; the only required parameter is `SRC_DIR`, which
is used both as the Docker build context and as the source bind mount:

```sh
SRC_DIR="$HOST_WORKSPACE/Anaglyfin" docker compose -f .ci/test.yml run --rm test
```

For worktrees where the compose CLI and the Docker daemon see different mount points,
use an ignored `.env` file and pass it explicitly:

```sh
docker compose --env-file .env -f .ci/test.yml run --rm test
```

Steps executed by the job: `dotnet restore` → `dotnet build -warnaserror` (Release)
→ `dotnet test` → `dotnet format whitespace --verify-no-changes` → publish the linux-x64
wrapper → `Anaglyfin.Packager pack` → `Anaglyfin.Packager verify`.

The runtime-validation stack in `dev/jellyfin-validation/` is deliberately **not** wired into
this job. It pulls the Jellyfin image, it needs a library and a client, and its results are
observations rather than a gate; the job above stays fast, deterministic, and network-free.

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

## Packaging

The last two CI steps are the packaging gate, and they run on every branch: the job writes
`artifacts/Anaglyfin_<version>.zip` with `Anaglyfin.dll` at the archive root, `meta.json` holding
the manifest identity plus the archive's measured size and SHA-256, and
`artifacts/anaglyfin-ffmpeg` - the wrapper published self-contained for `linux-x64`. A missing
assembly, a manifest the build does not carry, an archive with the DLL nested in a directory, a
record whose checksum or size does not match the file, or a wrapper that is not a Linux x86-64
ELF binary all fail the job rather than being noted in the log.

`docs/install.md` covers where those three files go on a bare-metal server and in a container
that gets `./jellyfin/config` mounted at `/config`.

## License

Anaglyfin is free software and is licensed under the GNU General Public License, version 3 only
(`GPL-3.0-only`). See [`LICENSE`](LICENSE) for the complete terms.

Copyright © 2026 Tom Thayer

Third-party components used at runtime, including FFmpeg-mvc when installed separately, remain
under their respective licenses and are not re-licensed by Anaglyfin.
