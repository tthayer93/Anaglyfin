# Installing Anaglyfin

Install the plugin from one repository URL, get three FFmpeg runtime files onto the server,
and point Jellyfin's FFmpeg at the wrapper. On Docker the commands in §3 run inside the running
container and build both halves there. Target: Jellyfin **12.x** — Debian/Ubuntu package or
`jellyfin/jellyfin:12.1`, the container this flow currently targets. The plugin's
`targetAbi 12.0.0` is a minimum-version floor rather than an exact match, so `v0.2.1` loads on
Jellyfin 12.0 and on 12.1 alike.

## 1. Install the plugin

In **Dashboard → Plugins → Catalogs → Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalog, then **restart Jellyfin**: a plugin is loaded at startup.

That installs the plugin only — no runtime binaries. The server wants those in place before it next
starts, and §2 says where each shape gets them.

**No catalog?** If that repository URL cannot be added — the server has no route to
`raw.githubusercontent.com`, say — unpack `Anaglyfin_<version>.zip` from the
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into
`/var/lib/jellyfin/plugins/Anaglyfin` or `./jellyfin/config/plugins/Anaglyfin`; on a package
install, run `sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin`, then restart.
The catalog installs that same archive, so the plugin is identical either way.

## 2. Runtime files

All three live in **one** directory — `/config/anaglyfin/ffmpeg` on Docker, `/opt/anaglyfin/ffmpeg`
on bare metal — named exactly:

- `anaglyfin-ffmpeg` — the Anaglyfin wrapper, published as a self-contained linux-x64
  [release asset](https://github.com/tthayer93/Anaglyfin/releases). The Docker commands below
  currently publish it from the tagged source instead of downloading that asset; bare metal takes
  the asset itself (§4).
- `ffmpeg-mvc` — a Jellyfin-compatible [FFmpeg-mvc](https://github.com/tthayer93/FFmpeg-mvc) build; target `n8.1.2-mvc8-jf5`, the `jellyfin-8.1` product line's queue sync to `jellyfin-ffmpeg v8.1.2-5`. Its FFmpeg base stays **8.1.2**, which is why the server still logs `Found ffmpeg version 8.1.2` (§5); the plain-line `n8.1.3-mvc8` tag carries no Jellyfin queue and is not a supported product target. This is the binary the wrapper dispatches marker/profile commands to.
- `ffprobe` — the `ffprobe` from the **official** Jellyfin FFmpeg, *not* the one from the FFmpeg-mvc
  build. The custom build does produce its own `ffprobe`, but it must not be installed beside the
  wrapper: the server probes files through this `ffprobe`, and the official one matches the stock
  FFmpeg the server runs ordinary playback on.

Jellyfin finds `ffprobe` **beside the FFmpeg path it was given** — beside the wrapper — never on `PATH`.
The wrapper is what the server's FFmpeg path points at, so the official `ffprobe` goes into the same
directory as the wrapper and `ffmpeg-mvc`.
The two server shapes differ only in who fills that directory:

- **Docker:** a series of `docker compose exec` commands against the running container compiles
  FFmpeg-mvc and publishes the wrapper into `/config/anaglyfin/ffmpeg`, linking the two FFmpeg
  binaries onto the FFmpeg runtime libraries the official image already ships. The Docker host places
  nothing, and the container needs no extra packages to run what the commands produce.
- **Bare metal:** you install all three yourself (§4).

## 3. Docker server (recommended)

Every command below runs **inside the running container**. Nothing is installed on the Docker host
and nothing extra is mounted for the runtime. Assumes the service is named `jellyfin`, the image is
`jellyfin/jellyfin:12.1`, and `./jellyfin/config` is mounted at `/config`. Every command below
execs as **root** with `-u root` — that is what the package install and the writes under `/config`
need — rather than trusting the user the container happens to run its own process as, which the
image can be configured to change. This flow targets `linux-x64`, because the wrapper publish
targets that RID.

Three things are involved, in three different places, and the sequence turns on which is which:

- the three runtime files, plus the `lock` and `wrapper` directories, go under `/config` — the
  persisted volume, so they outlive the container;
- the apt packages are **build-time only**. They are the compiler, `curl`, the headers and the
  `pkg-config` data the two builds read, and they live in the container's own filesystem, so a
  recreate discards them. Nothing installed at run time needs them: they come back only when §3.2
  recompiles something;
- the libraries the encoder loads when it runs come from the official image itself: mostly the
  `/usr/lib/jellyfin-ffmpeg/lib` set its own FFmpeg uses, and for the rest the Debian libraries the
  image already carries. Step 4 links the compiled `ffmpeg-mvc` onto that first directory, and drops
  the image's own `ffprobe` beside the wrapper, so a recreated container runs the runtime files with
  no packages installed.

**GPU access.** The three things above cover the install itself; the host GPU is a separate concern.
The official image ships the Intel driver components — the `iHD`/`i965` VA-API drivers and
`libva`/`libvpl` under `/usr/lib/jellyfin-ffmpeg/lib` — but Docker does not hand the container the
host GPU unless you map it, so add this to the service:

```yaml
devices:
  - /dev/dri
```

It is required for Intel VA-API (`h264_vaapi`) and QSV (`h264_qsv`) **hardware encoding**: without the
mapping the runtime still installs and software encoding still works, but a hardware profile cannot
run. Ordinary (software) playback needs nothing extra here.

### 3.1 Initial install

**1. Dependencies** — the compiler, `curl`, `make`, and the `-dev` packages both builds read, plus
the ICU the wrapper globalises with. This is the **build** half of the install and nothing more: the
binaries it produces run on the libraries inside the image (step 4), not on these. Run it again
whenever §3.2 rebuilds something in a container that has since been recreated.

```sh
docker compose exec -u root jellyfin sh -c '
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    build-essential \
    ca-certificates \
    curl \
    make \
    nasm \
    pkg-config \
    libass-dev \
    libdrm-dev \
    libfontconfig-dev \
    libfreetype-dev \
    libfribidi-dev \
    libicu-dev \
    libnuma-dev \
    libva-dev \
    libvpl-dev \
    libx264-dev \
    zlib1g-dev
'
```

**2. Directories** — one for the three files, two for what the wrapper writes into.

```sh
docker compose exec -u root jellyfin install -d \
  /config/anaglyfin/ffmpeg \
  /config/anaglyfin/lock \
  /config/anaglyfin/wrapper
```

**3. FFmpeg-mvc: fetch and unpack.** `/tmp` is build space inside the container; none of it is
needed once the three files are in place, and none of it survives a recreate.

```sh
docker compose exec -u root jellyfin curl -fsSL \
  https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.2-mvc8-jf5.tar.gz \
  -o /tmp/ffmpeg-mvc.tar.gz

docker compose exec -u root jellyfin sh -c '
  rm -rf /tmp/ffmpeg-mvc-src
  mkdir -p /tmp/ffmpeg-mvc-src
  tar -xzf /tmp/ffmpeg-mvc.tar.gz -C /tmp/ffmpeg-mvc-src --strip-components=1
'
```

**4. FFmpeg-mvc: compile and install.** The compile produces both `ffmpeg` and `ffprobe`, but only
the `ffmpeg` half is installed here, as `ffmpeg-mvc`, into the directory the wrapper lives in. The
`ffprobe` that directory needs is the image's **official** one, which step 4 copies in beside the
wrapper — it does **not** install the build's own `ffprobe`. The compile is the slow part of this
sequence.

The last two `configure` lines are what makes the result outlive the container that built it. The
official image ships its own FFmpeg runtime libraries — `libass`, `libva`, `libva-drm`, `libvpl`,
the font stack, and the VA-API drivers under `dri/` — in `/usr/lib/jellyfin-ffmpeg/lib`; baking that
directory into the binary as its RUNPATH means `ffmpeg-mvc` loads them from there at run time,
instead of from anything apt put in the container.

```sh
docker compose exec -u root jellyfin sh -c '
  cd /tmp/ffmpeg-mvc-src
  ./configure \
    --disable-doc \
    --enable-gpl \
    --enable-libx264 \
    --enable-libass \
    --enable-vaapi \
    --enable-libvpl \
    --extra-ldflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib \
    --extra-ldexeflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib
'

docker compose exec -u root jellyfin sh -c '
  cd /tmp/ffmpeg-mvc-src
  make -j"$(nproc)"
'

docker compose exec -u root jellyfin sh -c '
  install -m 0755 /tmp/ffmpeg-mvc-src/ffmpeg /config/anaglyfin/ffmpeg/ffmpeg-mvc
  # ffprobe beside the wrapper is the image'"'"'s OWN (official Jellyfin) build, not the one this
  # compile wrote. The server probes through it and it must match the stock FFmpeg. The build'"'"'s
  # own /tmp/ffmpeg-mvc-src/ffprobe is deliberately left unused.
  install -m 0755 /usr/lib/jellyfin-ffmpeg/ffprobe /config/anaglyfin/ffmpeg/ffprobe
'
```

**5. The .NET SDK**, into `/tmp` for the same reason. The wrapper publishes self-contained, so the
server itself never needs a .NET runtime.

```sh
docker compose exec -u root jellyfin curl -fsSL https://dot.net/v1/dotnet-install.sh \
  -o /tmp/dotnet-install.sh

docker compose exec -u root jellyfin bash /tmp/dotnet-install.sh \
  --channel 10.0 \
  --install-dir /tmp/dotnet
```

**6. The wrapper: fetch and unpack.** `v0.2.1` is a tag, not a branch — the source arrives as a tag
archive.

```sh
docker compose exec -u root jellyfin curl -fsSL \
  https://github.com/tthayer93/Anaglyfin/archive/refs/tags/v0.2.1.tar.gz \
  -o /tmp/anaglyfin.tar.gz

docker compose exec -u root jellyfin sh -c '
  rm -rf /tmp/anaglyfin-src
  mkdir -p /tmp/anaglyfin-src
  tar -xzf /tmp/anaglyfin.tar.gz -C /tmp/anaglyfin-src --strip-components=1
'
```

**7. The wrapper: publish and install.** The SDK on `PATH` and the NuGet cache both live inside this
one `sh -c`, because neither survives into a separate `exec`.

```sh
docker compose exec -u root jellyfin sh -c '
  cd /tmp/anaglyfin-src
  export PATH=/tmp/dotnet:"$PATH"
  export DOTNET_CLI_HOME=/tmp
  export NUGET_PACKAGES=/tmp/anaglyfin-nuget
  dotnet publish src/Anaglyfin.FFmpegWrapper/Anaglyfin.FFmpegWrapper.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    --output /tmp/anaglyfin-wrapper
'

docker compose exec -u root jellyfin install -m 0755 \
  /tmp/anaglyfin-wrapper/Anaglyfin.FFmpegWrapper \
  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

**8. Ownership.** Everything above ran as root and left a root-owned tree, but the server writes the
concurrency slots and the settings document into it — and so does whoever owns the rest of `/config`
from the host side. Hand the tree to the owner of `/config`: on the official image that is root and
this is a no-op, and on a `./jellyfin/config` owned by a host user it is what lets the server write
its locks.

```sh
docker compose exec -u root jellyfin sh -c \
  'chown -R "$(stat -c "%u:%g" /config)" /config/anaglyfin'
```

**9. Environment** — add to the service:

```yaml
environment:
  JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
  ANAGLYFIN_SERVER_FFMPEG: /usr/lib/jellyfin-ffmpeg/ffmpeg
  ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
  ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

The two FFmpeg variables split the work: `ANAGLYFIN_REAL_FFMPEG` is the FFmpeg-mvc build the
wrapper hands marker/profile commands to, and `ANAGLYFIN_SERVER_FFMPEG` is the stock FFmpeg the
official image ships — the one the server would have run — which now receives every ordinary command
and Jellyfin's startup capability probes. That is the whole point of naming a second binary: a
codec or tone-mapping tool the server reads off its FFmpeg is then genuinely present for the
commands built around it. Leave `ANAGLYFIN_SERVER_FFMPEG` unset and the older single-binary
behaviour stays — every command, ordinary and marker alike, goes to `ANAGLYFIN_REAL_FFMPEG`.

One consequence to check on a capability-limited build: because the startup probes now report the
**official** set, Jellyfin may reach for an encoder or tone-mapping filter for a *marker* job that a
minimal FFmpeg-mvc build does not carry. Verify that the `ffmpeg-mvc` binary has the encoder Jellyfin
actually selects (compare `ffmpeg-mvc -encoders` against the official `/usr/lib/jellyfin-ffmpeg/ffmpeg
-encoders`). For the documented QSV/VA-API H.264 flow — `configure --enable-vaapi --enable-libvpl
--enable-libx264` — it is adequate.

One case of that check is already answered in code, because it has an exact signature: if the
transcode log shows `Unrecognized option 'vbr:a'` and `Error splitting the argument list` with
nothing else after the child's banner, the official build's probe advertised `libfdk_aac` (which it
carries and the FFmpeg-mvc build does not), Jellyfin wrote `-codec:a… libfdk_aac` plus the
encoder's private `-vbr:a <n>` into the command, and the minimal build died parsing it. That is the
audio capability inversion, and the fixed wrapper - `v0.2.1` and later; a `v0.2.0` wrapper still
dies this way - handles this known case on the marker route: a rewritten marker command has that
selection mapped to native `aac`, the `vbr` option
removed and a bitrate synthesized from a fixed table, with an `anaglyfin-wrapper: warning:` line
naming the fallback. Ordinary and single-binary commands keep their fdk tokens verbatim — their
binary has the encoder — and any *other* capability gap (video encoders, filters) stays exactly the
manual `-encoders` diff above: the wrapper only maps this one known audio selection, so a build
that differs from the official set elsewhere still needs build-flag parity or the device profile
fixed, not a wrapper change.

**10. Recreate** the service so it starts with that environment:

```sh
docker compose up -d --force-recreate jellyfin
```

**11. Restart Jellyfin:**

```sh
docker compose restart jellyfin
```

which is also what loads the plugin from §1, if it was waiting to install or update.

Nothing from steps 1–7 has to be repeated after the recreate, and that is the whole point of the RPATH
in step 4. The recreate throws away the build packages, but those only ever fed the compiler; what the
server runs is the three files under `/config`, which the recreate leaves exactly where they were, and
the FFmpeg runtime libraries the image ships in `/usr/lib/jellyfin-ffmpeg/lib`, which come back with
the image. A recreate is `up -d --force-recreate`, an image pull, or `down` and `up`; a plain
`docker compose restart` is **not** one, and needs nothing either.

The one thing that can genuinely move under this arrangement is the image itself: a new
`jellyfin/jellyfin` release carries its own copy of those libraries, so if a pull ever changes what
`ffmpeg-mvc` resolves against — `docker compose exec -u root jellyfin ldd
/config/anaglyfin/ffmpeg/ffmpeg-mvc` will say `not found` — that is the moment to redo steps 3–4.

### 3.2 Upgrade

The same commands, against the running container, one component at a time. Both install over the
top of the same three filenames, so nothing has to be uninstalled or removed first.

- **The wrapper** — repeat steps 5–7 with the new tag in the source URL (step 5 is only needed if
  `/tmp/dotnet` is gone, which a recreate does to it).
- **FFmpeg-mvc** — repeat steps 3–4 with the new tag in the source URL; the `ffmpeg` the compile
  produces becomes `ffmpeg-mvc`, and step 4 re-copies the image's own `ffprobe` beside it (the build's
  own `ffprobe` is not installed). Keep both `-rpath` lines in the `configure`
  command — they are what lets the new build keep running on the image's own libraries.
- **Either half, if the container has been recreated since the build you are replacing** — run the
  step 1 dependency block first. That is a rebuild, and a rebuild needs the compiler and the headers
  the recreate took away; it is the only case in which that block is ever run a second time.

A container that was only restarted since the last build needs nothing ahead of either half. Once
the files are under `/config`, no dependency block is a runtime requirement at all: apt is for
building, the image is for running.

Step 8 (ownership) is **not part of a routine upgrade.** A routine upgrade overwrites only the
executable files — the `install -m 0755` lines in steps 4 and 7 — and never the `lock` or `wrapper`
directory. The new binaries come out mode `0755`, so the server keeps execute access to them whatever
their owner, and the two directories it writes its concurrency slots and settings document into are
untouched, so their owner is still what step 8 set. An ordinary upgrade never demands step 8; act on
the ownership state or symptom you actually observe, not on the fact that an upgrade ran. The states
that justify re-running it are: the container's or host's UID/GID arrangement has changed since the
install, the `lock` or `wrapper` directory has been recreated root-owned by something outside this
flow, or the log reports permission errors writing the slots or the settings document. Even then it
is optional, and step 8 above carries the command.

Then:

```sh
docker compose restart jellyfin
```

### 3.3 Uninstall

```sh
docker compose exec -u root jellyfin rm -rf /config/anaglyfin
```

Take **every** Anaglyfin environment entry out of the compose file, not just some of them. That is
all five step 9 added — `JELLYFIN_FFMPEG`, `ANAGLYFIN_REAL_FFMPEG`, `ANAGLYFIN_SERVER_FFMPEG`,
`ANAGLYFIN_LOCK_DIR`, `ANAGLYFIN_WRAPPER_SETTINGS` — plus any optional one added alongside them, such
as `ANAGLYFIN_SLOT_WAIT_MS` or the `ANAGLYFIN_OFFICIAL_FFMPEG` alias, if present. `JELLYFIN_FFMPEG`
is the one that bites: leave it pointing at the wrapper just deleted and Jellyfin looks for its
FFmpeg at a path that no longer exists and fails to find it. Then recreate with
`jellyfin/jellyfin:12.1`:

```sh
docker compose up -d --force-recreate jellyfin
```

That is the stock container again: its own FFmpeg, the runtime files gone with `/config/anaglyfin`,
and any build packages from step 1 discarded with the container they were installed into — they were
never part of the runtime, so there is nothing else to undo. Take the plugin out of Dashboard →
Plugins too if it should go with it.

## 4. Bare-metal server

Same grouping under `/opt/anaglyfin/ffmpeg`; `lock` and `wrapper` under the server data directory,
everything owned by the `jellyfin` user:

```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin ./anaglyfin-ffmpeg  /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
sudo install -m 0755 -o jellyfin -g jellyfin /path/to/ffmpeg-mvc /opt/anaglyfin/ffmpeg/ffmpeg-mvc
# ffprobe beside the wrapper is the STOCK Jellyfin ffprobe, not the FFmpeg-mvc one. On the
# Debian/Ubuntu package that is the ffprobe beside the server's own FFmpeg:
sudo install -m 0755 -o jellyfin -g jellyfin /usr/lib/jellyfin-ffmpeg/ffprobe /opt/anaglyfin/ffmpeg/ffprobe
sudo install -d -m 0755 -o jellyfin -g jellyfin /var/lib/jellyfin/anaglyfin/lock /var/lib/jellyfin/anaglyfin/wrapper
```

Pass those five variables in a systemd drop-in. Create the drop-in directory first:

```sh
sudo install -d /etc/systemd/system/jellyfin.service.d
```

```ini
# /etc/systemd/system/jellyfin.service.d/anaglyfin.conf
[Service]
Environment=JELLYFIN_FFMPEG=/opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
Environment=ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc
# The stock FFmpeg this server would otherwise run. On the Debian/Ubuntu package that is
# the FFmpeg Jellyfin ships beside the ffprobe you installed above; if the server was
# pointed at a different FFmpeg before Anaglyfin, name that one instead of guessing.
Environment=ANAGLYFIN_SERVER_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
Environment=ANAGLYFIN_LOCK_DIR=/var/lib/jellyfin/anaglyfin/lock
Environment=ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

Apply it and restart:

```sh
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

Uninstalling this shape mirrors §3.3: delete this drop-in **and every other Anaglyfin environment
entry, wherever you added it** — an optional `ANAGLYFIN_SLOT_WAIT_MS` or the `ANAGLYFIN_OFFICIAL_FFMPEG`
alias may sit in some other drop-in, not this one — then `sudo systemctl daemon-reload`, restart
Jellyfin, and remove the `/opt/anaglyfin` runtime files and the `/var/lib/jellyfin/anaglyfin` `lock`
and `wrapper` directories those variables pointed at.

## 5. Verify and update

- `Dashboard → Plugins` lists **Anaglyfin** and its settings page opens.
- The server log names the wrapper as Jellyfin's FFmpeg, at the path the shape installed it to.
  Docker:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

  Bare metal:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: Found ffmpeg version 8.1.2
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

- The wrapper answers `-version` straight through to a real encoder. `-version` is an ordinary
command, so it is dispatched to `ANAGLYFIN_SERVER_FFMPEG` when that variable is set. To check that
the **FFmpeg-mvc** binary is reachable, the two snippets explicitly blank `ANAGLYFIN_SERVER_FFMPEG`
(a blank value counts as unset), which sends the ordinary command down the real/marker route so the
banner is the FFmpeg-mvc build's. Under the fork's convention — tag, VERSION and banner named
identically — the identity the target build is expected to print is
`ffmpeg version n8.1.2-mvc8-jf5`, and the server parses it as `8.1.2`, which is the version the two
log excerpts above expect. Docker:

  ```sh
  docker compose exec -u root jellyfin \
    env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/config/anaglyfin/ffmpeg/ffmpeg-mvc \
    /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
  ```

  Bare metal:

  ```sh
  sudo -u jellyfin env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc \
    /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
  ```

Which binary a missing file stops depends on the deployment shape. In a **two-binary** deployment
(`ANAGLYFIN_SERVER_FFMPEG` set), the server's own startup probe and ordinary playback reach the stock
FFmpeg, so the server starts and ordinary playback succeeds even when the FFmpeg-mvc binary named by
`ANAGLYFIN_REAL_FFMPEG` is missing — only marker/profile jobs then fail, with a refusal naming
`ANAGLYFIN_REAL_FFMPEG`. In a **single-binary** deployment (`ANAGLYFIN_SERVER_FFMPEG` unset), every
command including that startup probe falls back to `ANAGLYFIN_REAL_FFMPEG`, so it is a missing
`ANAGLYFIN_REAL_FFMPEG` that keeps the server from passing its FFmpeg validation. Full checks:
`docs/validation.md`.

Update the plugin from the catalog and restart Jellyfin. The three runtime files are the server's
own, and the plugin tracks none of them:

- **Docker:** repeat the commands for the half you are moving (§3.2) and restart.
- **Bare metal:** swap the file under `/opt/anaglyfin/ffmpeg/` by hand and restart.
