# Anaglyfin

Jellyfin plugin that exposes 3D MVC sources as selectable playback versions
(2D base, red-cyan anaglyph, full SBS, half SBS), played back through the normal
Jellyfin HLS pipeline.

Runtime design and validation notes live in `docs/architecture.md` and
`docs/validation.md`. Installation steps - bare-metal and Docker - are in `docs/install.md`.
Orchestrator planning notes are not tracked in this checkout.

## Installing it on a server

Add the Anaglyfin plugin repository in **Dashboard -> Plugins -> Catalogs -> Repositories -> Add
plugin repository**, install **Anaglyfin** from the catalogue, and restart Jellyfin:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

That installs the plugin only. The FFmpeg entry point - `anaglyfin-ffmpeg`, plus a Jellyfin-compatible
`ffmpeg-mvc` and its matching `ffprobe` - is installed separately on both server shapes and is never
bundled in or installed by the plugin: on Docker by the documented `docker compose exec` commands,
which compile both halves inside the running container and link the persistent FFmpeg binaries against
the FFmpeg runtime libraries the official `jellyfin/jellyfin` image already ships - no helper script,
no Dockerfile, and no packages left behind for the runtime to need - and on bare metal by hand. The
one-page quickstart (Docker and bare-metal) is in `docs/install.md`.

## Current state

The source tree now contains the working code path, not just the bootstrap scaffold:

- fixed plugin identity, manifest, and service registration
- profile catalog with the shipped 2D, SBS, anaglyph, and custom grayscale profiles
- plugin configuration model and dashboard admin page
- conservative MVC detection rules
- alternate media source provider that adds profile-marked playback versions
- one global default profile, offered first to every client ahead of the remaining enabled ones
- one admin switch over the raw 3D MVC file's place in the version pickers, on by default
- profile marker and parser contract
- exact FFmpeg profile argument builder
- out-of-process `Anaglyfin.FFmpegWrapper` executable with marker rewrite,
  concurrency guard, and real FFmpeg launcher
- `tools/Anaglyfin.Packager`, which writes the plugin zip, `meta.json`, and the staged
  linux-x64 wrapper, and refuses a build whose assembly and manifest disagree
- `.github/workflows/release.yml`, which turns a pushed version tag into the GitHub release and
  the Jellyfin plugin-repository manifest servers install from
- xUnit tests covering the pure seams, the command contract, and the packaging job

Real Jellyfin runtime validation is tracked separately in `docs/validation.md`, and the install
shapes for a bare-metal server and for `jellyfin/jellyfin:latest` are in `docs/install.md`.
Before either is visited for real, `dev/jellyfin-validation/` runs the packaged plugin and
wrapper against a disposable `jellyfin/jellyfin:latest` mounted the way the target server is.

The default profile is global and only global. The version an administrator picks as the default
is offered first to every client - phone, TV, web, VR - with the remaining enabled versions
following in catalog order, and the same ordering is what materialised version items are ranked
by. A default only chooses which version is offered first; it authorises no playback and changes
no library item. Exact-device default overrides existed on pre-release builds only: the provider
once read the request's `Jellyfin-DeviceId` claim so one registered device could start on a
different version, and that feature was removed before this release. The provider now consults
nothing about the request itself. Settings XML written by those pre-release builds loads intact -
a stale `DeviceDefaultProfiles` element decides neither the default nor the offered order, and it
is dropped the next time the settings are saved.

The admin page has one switch over the original file itself:

```text
Offer original 3D MVC version
```

It ships checked, because offering the raw file is what every build before the setting did and what
a settings file that predates it says nothing about. Checked, the raw MVC file the scanner filed
beside a movie is offered as a version of that movie. Unchecked, that one raw source is left out of
the two lists a client picks a version from - the details-page version list and PlaybackInfo - and
nothing else moves:

- Anaglyfin's converted versions of that file stay offered, in the same order, because the converted
  versions are what the switch is an alternative to rather than a replacement for.
- The MVC library item, its alternate-version link to the movie it was filed beside, its path and its
  resume state are untouched. The switch is a read filter over the version lists, which is why saving
  it back brings the entry back on the next request: no library scan, no re-link, nothing re-scraped.
- An entry is only ever left out when a converted version of that same file is offered in its place, so
  the switch cannot leave a film with a version taken away and none added.
- An MVC movie asked about as itself keeps its own source: the entry this switch takes out of a list is
  a sibling version of the item being asked about, never that item's own entry.
- While an entry is out of those lists, a client cannot choose it through them. A PlaybackInfo request
  naming the hidden source id is answered the way the server answers any request for a source that is
  not in the list, and turning the switch back on restores it. This is the setting's known boundary, and
  the item, its source and its data are intact on the other side of it.

No branch of that list writes to the library, so the switch needs no migration and no rescan in
either direction.

The remaining implementation follow-ups are documented in those files and include subtitle
ordinal wiring through the provider. Selecting by approximate device category - TV, phone,
tablet, VR headset, 3D-capable projector - is deferred future work and not a current capability:
Jellyfin 12 has no reliable native server-side device type to key one on, so such a feature can
only ever be an explicitly labelled heuristic over what clients report about themselves, layered
over the one global default this release ships.

## Requirements

| Item | Value |
| --- | --- |
| .NET SDK | `10.0.x` (pinned by `global.json`, `rollForward: latestMinor`) |
| Target framework | `net10.0` |
| Jellyfin ABI | `Jellyfin.Controller` / `Jellyfin.Model` `12.0.0` |
| FFmpeg runtime | FFmpeg-mvc `jellyfin-8.1` build, reached through the wrapper |

## Repository layout

```text
.ci/                           CI container definition, and the release manifest helper
.github/workflows/             Release pipeline: a version tag becomes a release and a manifest
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
  It registers the profile catalog, MVC detector, and settings source, appends the version-picker
  filter over the server's own media source manager, and must keep a public parameterless
  constructor.
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

`docs/install.md` covers where those three files go on a bare-metal server, and the
`docker compose exec` commands that build them inside a running Jellyfin container instead - onto
the FFmpeg libraries that image already ships, so a recreated container still runs them.

## Releasing a version

A release is one annotated version tag: `v0.1.0`, `v0.1.1`, and so on. `.github/workflows/release.yml`
reacts to it and does the rest - it rebuilds the tagged commit through the same gate every branch
passes, creates or refreshes the GitHub release with its three assets, and republishes the manifest
every server reads:

| Asset | Role |
| --- | --- |
| `Anaglyfin_<version>.zip` | The plugin archive, and the only archive Jellyfin downloads |
| `anaglyfin-ffmpeg` | The wrapper, as a release asset an administrator places by hand |
| `SHA256SUMS.txt` | The SHA-256 of both files, for people; the server verifies the zip's MD5 |

The manifest lives on a long-lived `metadata` branch as one file, and is served from
`https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json`. That address is the
permanent one: it is what an administrator types once, it does not change with the version, and it
needs no Pages setup. `.ci/release-manifest.sh` builds it from the release's own `meta.json`, keeps
every version already published, replaces only the entry for the version being republished, and
refuses to write a document a server would not install. The release description is the changelog a
server shows; with no description the entry says `Anaglyfin <version>`.

Two rules bound this pipeline. It never creates and never pushes a tag - it publishes a tag that
already exists, and a malformed tag is rejected before anything is built. And the historical `cp*`
checkpoint tags are local to whoever made them: they are not release inputs, and they are not
restored to the repository.

## License

Anaglyfin is free software and is licensed under the GNU General Public License, version 3 only
(`GPL-3.0-only`). See [`LICENSE`](LICENSE) for the complete terms.

Copyright © 2026 Tom Thayer

Third-party components used at runtime, including FFmpeg-mvc when installed separately, remain
under their respective licenses and are not re-licensed by Anaglyfin.
