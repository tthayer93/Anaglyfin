# Installing Anaglyfin on Jellyfin 12

Anaglyfin is two parts on every server: the plugin, which the server loads, and the FFmpeg wrapper
binary, which the server starts in place of FFmpeg. The plugin arrives by itself once an
administrator has pointed the server at one URL; the wrapper is always deployed by hand, on both
shapes. Everything else - plugin behaviour, profile selection, marker transport - is identical on
a bare-metal host and in the official container. The difference is only where files are put and how
the server is told to look at them.

Target runtime: Jellyfin `12.0.0`, plugin `net10.0`, `targetAbi 12.0.0`, wrapper for Linux
`amd64` (Debian-based `jellyfin/jellyfin:latest`). Older Jellyfin lines are not targeted.

## Adding the Anaglyfin plugin repository

The ordinary route needs no download at all. Go to **Dashboard -> Plugins -> Catalogs**, open
**Repositories**, choose **Add plugin repository**, and fill in the two fields it asks for:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

That URL is the whole public face of the release pipeline. It is one file on a long-lived
`metadata` branch of this repository - deliberately not GitHub Pages and deliberately not a file
attached to one release, because those either need setup or move when the version does. The
address an administrator saves never changes; the versions listed behind it do.

Anaglyfin appears in the plugin catalogue on the next visit. Press **Install**, then **restart the
server**: Jellyfin reads a newly installed plugin assembly during startup, not during the install,
so the plugin is not in place until the process has been restarted. Dashboard -> Plugins then lists
it, and the settings page appears in the dashboard's own settings menu.

Two parts of this flow are expected and are not a problem with the package:

- **The third-party repository warning.** Every repository that is not `repo.jellyfin.org` gets the
  same disclaimer before an install - that the plugin comes from a repository the server does not
  know and that installing from it is at your own risk. Anaglyfin is not published on the official
  repository, so this always asks. It is a statement about provenance, and it is the same warning
  any community plugin from a personal repository raises.
- **A few minutes of staleness right after a release.** That URL is served from a CDN which keeps
  the previous copy for a short while after a publication, so a release published in the last few
  minutes may not be in the list yet. Open the catalogue again later; there is nothing to clear on
  the server.

**One repository, one archive, bare-metal and Docker alike.** Both server shapes use exactly the
URL above and both receive the same plugin zip; there is no container build of the plugin, and
nothing in the plugin is specific to one shape. What the repository install does is deliver the
plugin - the alternate versions, the admin page, the settings document the plugin writes. That is
one of Anaglyfin's two parts.

**The Anaglyfin FFmpeg entry point is deployed separately on both shapes, and the plugin does not
install it.** `anaglyfin-ffmpeg` is published as its own release asset precisely because it is a
file an administrator copies to disk and points the server at; it is never inside the plugin
archive, and Jellyfin never downloads it. Nor is FFmpeg-mvc - the real encoder the wrapper hands
commands to - bundled, downloaded, or installed by anything here: it is a separate build the
administrator installs and names, and without it the server will not start at all. The sections
below cover that deployment; installing the plugin does not replace any of it.

### Where the published files are

| What | Where |
| --- | --- |
| Plugin repository manifest - the one file the server reads | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |
| Plugin archive - the only archive Jellyfin downloads | `https://github.com/tthayer93/Anaglyfin/releases/download/v0.1.0/Anaglyfin_0.1.0.zip` |
| FFmpeg wrapper - a release asset, never plugin payload | `https://github.com/tthayer93/Anaglyfin/releases/download/v0.1.0/anaglyfin-ffmpeg` |
| SHA-256 digests, for people to check with | `https://github.com/tthayer93/Anaglyfin/releases/download/v0.1.0/SHA256SUMS.txt` |

The wrapper is a release asset rather than something only a person who cloned this tree can get,
for exactly the reason above: an administrator who installed the plugin from the repository and
never built it still needs that file, and the release is where it comes from. `ffprobe` and the
FFmpeg-mvc build do not come from here at all - they come from whichever FFmpeg-mvc release the
administrator decides to run, and the version deployed for the current subtitle-depth target is
recorded below.

Pushing a version tag is the whole release procedure: `v0.1.0` runs `.github/workflows/release.yml`,
which rebuilds the tagged commit through the same gate every branch passes, creates or refreshes
the GitHub release and its three assets, and replaces that one version's entry in the manifest on
`metadata` while leaving every older version listed. Neither the release nor the manifest is
written by hand.

The `checksum` a server verifies a download against is the zip's **MD5**, because MD5 is what
Jellyfin 12 hashes a plugin download with before it unpacks it. The SHA-256 digests in
`SHA256SUMS.txt` are for an administrator who wants to check a file; the server never reads them.

### If the server cannot reach that URL

A server with no outbound HTTPS to `raw.githubusercontent.com` cannot use the catalogue at all -
the manifest is fetched live every time the catalogue is opened, and a repository that will not
load is an error toast rather than an empty list. The offline route is the same two files: build
or download the artifacts, and place them by hand. Everything from here on is that route, and a
server that installed from the repository still needs all of it except the plugin-unpacking step
in each section - the wrapper, `ffprobe` beside it, the FFmpeg-mvc build, and the environment the
wrapper reads.

## What the packaging job produces

Run it through the CI container (`.ci/test.yml`), or with a .NET 10 SDK from the repository root:

```sh
dotnet build Anaglyfin.sln --configuration Release

dotnet publish src/Anaglyfin.FFmpegWrapper/Anaglyfin.FFmpegWrapper.csproj \
  --configuration Release --runtime linux-x64 --self-contained true -p:PublishSingleFile=true

dotnet run --project tools/Anaglyfin.Packager --configuration Release --no-build -- pack \
  --plugin-dll src/Anaglyfin/bin/Release/net10.0/Anaglyfin.dll \
  --manifest src/Anaglyfin/Plugin.manifest.xml \
  --output artifacts \
  --wrapper-input src/Anaglyfin.FFmpegWrapper/bin/Release/net10.0/linux-x64/publish/Anaglyfin.FFmpegWrapper

dotnet run --project tools/Anaglyfin.Packager --configuration Release --no-build -- verify \
  --manifest src/Anaglyfin/Plugin.manifest.xml \
  --artifacts artifacts \
  --wrapper-file anaglyfin-ffmpeg
```

| Artifact | What it is |
| --- | --- |
| `artifacts/Anaglyfin_0.1.0.zip` | The plugin archive. `Anaglyfin.dll` sits at the archive root, which is the only thing in it: the identity manifest and the admin page are embedded resources. |
| `artifacts/meta.json` | The install record. Identity fields come from `src/Anaglyfin/Plugin.manifest.xml`; archive name, assembly file name, size and SHA-256 are measured from the files themselves. |
| `artifacts/anaglyfin-ffmpeg` | The wrapper, published self-contained and single-file for `linux-x64`, staged with the executable bit set. |

The job names the archive after the manifest (`<name>_<version>.zip`), pins every entry timestamp
and compression level so the same build packages the same bytes, and refuses to produce anything
when the assembly, the manifest, and the record disagree. `verify` then judges the directory with
nothing but the manifest: archive layout, every metadata field, the recorded size and checksum,
and the wrapper's ELF header. See `docs/validation.md` for what still has to be checked on a real
server.

If the host already runs the .NET 10 runtime, a framework-dependent wrapper is also usable and is
much smaller. Publish it without a runtime identifier and start it as
`dotnet Anaglyfin.FFmpegWrapper.dll`, or with its own apphost:

```sh
dotnet publish src/Anaglyfin.FFmpegWrapper/Anaglyfin.FFmpegWrapper.csproj --configuration Release
# -> src/Anaglyfin.FFmpegWrapper/bin/Release/net10.0/publish/
```

The rest of this document assumes the self-contained `anaglyfin-ffmpeg`, because that is the form
that works inside the official container.

## Bare-metal server

The plugin goes into the `plugins` subdirectory of the server's **data directory**: whatever
`--datadir` points at, plus `plugins/Anaglyfin/`. That rule is the answer; the concrete directory
depends on how the server is started:

| Server | Data directory | Plugin directory |
| --- | --- | --- |
| Debian/Ubuntu package, which runs as the `jellyfin` user | `/var/lib/jellyfin` | `/var/lib/jellyfin/plugins/Anaglyfin/` |
| Started by your own login user, Linux defaults | `~/.local/share/jellyfin` | `~/.local/share/jellyfin/plugins/Anaglyfin/` |
| Anything started with `--datadir <dir>` | `<dir>` | `<dir>/plugins/Anaglyfin/` |

The package install's data directory belongs to the service user, so unpack it as root and hand
the directory straight back: Jellyfin has to read the assembly and write its own per-plugin data
next to it.

```sh
sudo install -d /var/lib/jellyfin/plugins/Anaglyfin
sudo unzip -o Anaglyfin_0.1.0.zip -d /var/lib/jellyfin/plugins/Anaglyfin
sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin
```

A server you start as yourself needs no `sudo` and no `chown`:

```sh
install -d ~/.local/share/jellyfin/plugins/Anaglyfin
unzip -o Anaglyfin_0.1.0.zip -d ~/.local/share/jellyfin/plugins/Anaglyfin
```

A server that installed Anaglyfin from the plugin repository already has that directory - the
server created it and owns it, which is the better outcome - and needs nothing from the two blocks
above. Everything below this point is still by hand either way, because installing the plugin
places no part of the FFmpeg entry point.

The wrapper is a file the server starts as its own user, so it wants a stable path and the
executable bit. Nothing writes to it, so readable-and-executable by anyone is the whole
requirement; giving the service user ownership of it is tidiness, not a necessity:

```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin anaglyfin-ffmpeg /opt/anaglyfin/ffmpeg/
```

The server looks for `ffprobe` **next to the FFmpeg path it was given**, not on `PATH`, so
the wrapper's directory needs one too - from the same FFmpeg-mvc build the wrapper is going
to hand commands to, so that probing and decoding agree:

```sh
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffprobe /opt/anaglyfin/ffmpeg/ffprobe
```

Then point the server at the Anaglyfin FFmpeg entry point - `JELLYFIN_FFMPEG`, the `--ffmpeg`
switch, or `<EncoderAppPath>` in `encoding.xml` - and install a Jellyfin-compatible FFmpeg-mvc
build as the binary it hands commands to. On a system-package install, where Jellyfin runs as a
systemd service, a drop-in is the place the server passes environment on to its helper processes:

```sh
# /etc/systemd/system/jellyfin.service.d/anaglyfin.conf
[Service]
Environment=JELLYFIN_FFMPEG=/opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
Environment=ANAGLYFIN_REAL_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg-mvc
Environment=ANAGLYFIN_LOCK_DIR=/var/lib/jellyfin/anaglyfin/lock
Environment=ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

```sh
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

`ANAGLYFIN_LOCK_DIR` is the one path here the service has to write for concurrency slots, so create
it for that user. `ANAGLYFIN_WRAPPER_SETTINGS` is the settings bridge between the plugin and the
wrapper: the plugin writes the JSON document there when it starts and when settings are saved, and
the wrapper reads the same path every time it starts. That document carries the two settings the
admin page owns that have no other way into the wrapper process: the subtitle-depth request and the
maximum number of concurrent Anaglyfin transcodes. If the variable is unset, the plugin falls back
to its own data directory - a path only the plugin knows - so the wrapper cannot see either setting
and playback stays flat on the shipped one-job limit. Both processes must therefore be able to reach
the one file; the directory it lives in must be writable by the Jellyfin service user.

`ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` is optional. Leave it unset when the admin page should decide
the limit; set it when one server needs a limit different from the setting saved for the product. A
valid value there overrides the page's number for every wrapper process that receives that
environment.

```sh
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/lock
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/wrapper
sudo -u jellyfin /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
```

## Docker server (`jellyfin/jellyfin:latest`)

Assumed mounts, which is the shape the runtime test target uses:

```text
./jellyfin/config -> /config
./jellyfin/cache  -> /cache
media             -> /media
```

Everything the plugin and the wrapper need has to be inside the container, and everything inside
the container that has to survive a recreate has to be under `/config`. `/config` is the natural
home for both, so nothing has to be copied into the image and nothing has to be installed in it.

Plugin - the server reads `/config/plugins`, so the archive unpacks there. This is the one step
the plugin repository replaces: a server that installed Anaglyfin from the catalogue has already
put it in this same directory, on the same mount, and can skip these two commands.

```sh
mkdir -p ./jellyfin/config/plugins/Anaglyfin
unzip -o artifacts/Anaglyfin_0.1.0.zip -d ./jellyfin/config/plugins/Anaglyfin
```

Wrapper - `JELLYFIN_FFMPEG` has to name a path *inside* the container:

```sh
mkdir -p ./jellyfin/config/anaglyfin/ffmpeg
cp artifacts/anaglyfin-ffmpeg ./jellyfin/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
chmod 0755 ./jellyfin/config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

`ffprobe` goes in that same directory. The server looks for it **beside the FFmpeg path it
was given**, not on `PATH`, so a wrapper standing alone is a server that starts and cannot
probe anything. Use the `ffprobe` from the same build the wrapper will hand commands to, so
probing and decoding agree:

```sh
cp /path/to/ffprobe ./jellyfin/config/anaglyfin/ffmpeg/ffprobe
chmod 0755 ./jellyfin/config/anaglyfin/ffmpeg/ffprobe
```

Inside a disposable container the image's own copy can be written there instead - same
directory, same reason:

```sh
docker compose exec jellyfin sh -c 'cp /usr/lib/jellyfin-ffmpeg/ffprobe /config/anaglyfin/ffmpeg/ffprobe'
```

The FFmpeg-mvc build the wrapper runs is mounted the same way - it is not in the official
image. The current subtitle-depth target is the official `n8.1.2-mvc7-jf4` build; an older
build without the `mvcsubdepth` filter cannot honour a depth request even when the wrapper
places one in the graph:

```sh
mkdir -p ./jellyfin/config/anaglyfin/ffmpeg-mvc
cp /path/to/ffmpeg-mvc-n8.1.2-mvc7-jf4 ./jellyfin/config/anaglyfin/ffmpeg-mvc/ffmpeg-mvc
chmod 0755 ./jellyfin/config/anaglyfin/ffmpeg-mvc/ffmpeg-mvc
```

The settings document also has to be shared by the plugin and every wrapper process that should see
the admin page's subtitle-depth request or concurrency limit. Put it under `/config`, where the
plugin can write it and the wrapper can read it:

```sh
mkdir -p ./jellyfin/config/anaglyfin/wrapper
```

Compose, with the wrapper as the server's FFmpeg:

```yaml
services:
  jellyfin:
    image: jellyfin/jellyfin:latest
    environment:
      JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
      ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg-mvc/ffmpeg-mvc
      ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
      ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
    volumes:
      - ./jellyfin/config:/config
      - ./jellyfin/cache:/cache
      - /path/to/media:/media
```

Add `ANAGLYFIN_MAX_CONCURRENT_TRANSCODES` only when this container should override the limit saved
on the admin page. When it is unset, the plugin's settings document is the ordinary route for that
setting.

Restart the container, then confirm from the host that the files are where the server will look:

```sh
docker compose exec jellyfin ls -l /config/plugins/Anaglyfin /config/anaglyfin/ffmpeg
docker compose exec jellyfin /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
```

The second command passes straight through to `ANAGLYFIN_REAL_FFMPEG`, so it doubles as the check
that the wrapper found the FFmpeg-mvc build.

### No package manager, no new packages

`jellyfin/jellyfin:latest` is the image the server runs in; the container the user runs is the
final test target, and it is not rebuilt by this repository. So:

- Do not `apt-get install` anything inside the container, at runtime or in a Dockerfile that
  layers on top of it. The plugin needs no dependency - Jellyfin 12 ships the assemblies it
  references - and the wrapper is self-contained, which is why it is published that way.
- Do not install the .NET runtime into the container for the wrapper. The framework-dependent
  build is offered for hosts that already have the runtime, not for this image.
- Do not replace the image's `ffmpeg` or `ffprobe`. Nothing under `/usr` is touched: the
  wrapper and the `ffprobe` beside it are added to the mounted `/config` directory, and only
  the server's FFmpeg path is repointed. That is also why the `ffprobe` next to the wrapper is
  a copy rather than a symlink to `/usr/lib/jellyfin-ffmpeg`: the server asks for a file in
  the wrapper's own directory, and a copy travels with the deployment.

## Getting a newer version

A new release appears behind the same URL, for a bare-metal server and for a container alike. The
manifest is refetched every time the catalogue is opened, so `Dashboard -> Plugins -> Anaglyfin`
grows a revision entry for it: every revision the manifest publishes is listed there, newest first,
each with its own install button, which is also how a server is moved back to an older revision.

Installing a revision is still an install: press it, then restart the server, because the assembly
is read at startup. Anaglyfin's install record asks for nothing on a server's behalf, so a new
version reaches a server the same way the first one did - by an administrator pressing Install.

A new version of the plugin changes nothing about the FFmpeg entry point. `anaglyfin-ffmpeg` is
published as its own release asset with every release, and installing a new plugin revision leaves
the wrapper on disk exactly where it was; swapping it is a separate, deliberate step, and the
FFmpeg-mvc build behind it is not part of this repository's output at all.

## After installing

Dashboard -> Plugins should list `Anaglyfin` with the description recorded in `meta.json`.

The settings page is edited from the dashboard's own settings menu, where it is listed under its
own name next to the server's entries. Jellyfin 12 lists a plugin page in that menu only when the
page asks to be listed there - the `EnableInMainMenu` flag on the page info served by
`/web/ConfigurationPages` - and a page that does not ask is reachable only from that plugin's own
entry on the Dashboard page. Anaglyfin's page asks, so the settings are not hidden behind a plugin
list an administrator has to think to open; `Dashboard -> Plugins -> Anaglyfin` opens the same page
too.

Opening the page's URL directly instead shows Anaglyfin's shipped defaults behind a warning: a
copy of the page outside the dashboard has no signed-in client to read the stored settings from or
save them back through, so it declines to edit them.

Both entries, and everything downstream of them - alternate versions on an MVC item, marker
transport, the rewritten FFmpeg command - are checked by the checklist in `docs/validation.md`.

In the server log, an install that worked says so before the dashboard is opened:

```text
Emby.Server.Implementations.Plugins.PluginManager: Loaded plugin: Anaglyfin 0.1.0.0
MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

The second and third lines together are the deployment contract: the server believes its FFmpeg
is the wrapper, and the wrapper let the server's version and capability probes through to the real
binary. A server whose `ANAGLYFIN_REAL_FFMPEG` names a path that is not there does not start at
all, and the complaint is filed against the wrapper:

```text
MediaEncoder: FFmpeg validation: The process returned no result
MediaEncoder: FFmpeg: Failed version check: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
MediaEncoder: FFmpeg: Path set by command line or environment variable is invalid
```

Both were observed on `jellyfin/jellyfin:latest` (12.0.0) in the disposable stack under
`dev/jellyfin-validation/`, which runs this document's Docker install and is described in
`dev/jellyfin-validation/README.md`.
