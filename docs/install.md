# Installing Anaglyfin

> **Scope:** This page installs Anaglyfin and its FFmpeg runtime files on a server where
> **Jellyfin 12 is already installed and running**. It is not a guide to installing Windows,
> Debian/Ubuntu, Docker, or Jellyfin itself. Install and configure the server and Jellyfin first
> through their own official documentation, then return here.

Install the plugin from one repository URL, get three FFmpeg runtime files onto the server,
and point Jellyfin's FFmpeg at the wrapper. On Docker the commands in §3 run inside the running
container and build both halves there. Target: Jellyfin **12.x** already installed on one of the
three server shapes this page documents — the official container `jellyfin/jellyfin:12.4` (§3),
which is the image this flow currently targets; the Debian/Ubuntu **package already installed**
(§4.1); and the official Windows server installer's service **already installed** (§4.2). Nothing
below installs Jellyfin; this page only configures Anaglyfin and its runtime files on top of it.
The plugin's `targetAbi 12.0.0` is a minimum-version floor rather than an exact match, so `v0.2.2`
loads on Jellyfin 12.0 or later. Jellyfin 12.4 updated its own .NET baseline to `10.0.14`; the plugin
targets `net10.0` and the wrapper publishes against the same band, so the 10.0.x SDK this page
installs still builds both and neither is retargeted for 12.4.

Which of those three this project has actually *run* is a separate question from which three it
documents, and the answer is in §4.2 rather than buried: every runtime result behind
`docs/validation.md` was observed on Linux, in the container and on the package. The plugin itself
is the same assembly on all three shapes; it is the FFmpeg runtime arrangement that differs.

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
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into the server's own plugin
directory: `/var/lib/jellyfin/plugins/Anaglyfin` on the Debian/Ubuntu package,
`./jellyfin/config/plugins/Anaglyfin` for a container that mounts `./jellyfin/config` at
`/config`, or `C:\ProgramData\Jellyfin\Server\plugins\Anaglyfin` on the Windows server installer
(that is `%ProgramData%\Jellyfin\Server\plugins\Anaglyfin`, the directory the service uses when it
installs from the catalog). On a package install, run
`sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/Anaglyfin`, then restart; on Windows
that tree is already the service's own data directory, so there is nothing to hand over — restart
the service (§4.2 step 7) and the plugin loads. The catalog installs that same archive, so the
plugin is identical either way.

## 2. Runtime files

All three live in **one** directory — `/config/anaglyfin/ffmpeg` on Docker, `/opt/anaglyfin/ffmpeg`
on Linux bare metal, `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg` on a Windows server — named
exactly:

- `anaglyfin-ffmpeg` — the Anaglyfin wrapper, published as a self-contained **linux-x64**
  [release asset](https://github.com/tthayer93/Anaglyfin/releases). That asset is the only wrapper
  binary the release publishes, and there is no `win-x64` one. The Docker commands below publish it
  from the tagged source instead of downloading it; Linux bare metal takes the asset itself (§4.1);
  and a Windows server has nothing to download, so the same tag is published for `win-x64` with
  `dotnet publish` on your own machine (§4.2 step 3).
- `ffmpeg-mvc` — a [FFmpeg-mvc](https://github.com/tthayer93/FFmpeg-mvc) build from the
  `jellyfin-8.1` branch; latest tag `n8.1.3-mvc10-jf1`. This is the binary the wrapper dispatches
  marker/profile commands to. That project publishes no binaries of any kind, Windows or otherwise,
  so on every shape below this one is either compiled by the commands given or built by you before
  you start.
- `ffprobe` — the `ffprobe` from the **official** Jellyfin FFmpeg, *not* the one from the FFmpeg-mvc
  build. The custom build does produce its own `ffprobe`, but it must not be installed beside the
  wrapper: the server probes files through this `ffprobe`, and the official one matches the stock
  FFmpeg the server runs ordinary playback on. On Windows that official `ffprobe.exe` is already on
  the box: the server installer bundles its FFmpeg flat into the program directory, so the stock
  pair is `C:\Program Files\Jellyfin\Server\ffmpeg.exe` and
  `C:\Program Files\Jellyfin\Server\ffprobe.exe`, and the second of those is what §4.2 step 5 copies
  beside the wrapper.

On Windows the same three names carry the `.exe` extension — `anaglyfin-ffmpeg.exe`,
`ffmpeg-mvc.exe`, `ffprobe.exe` — and none of those names is a preference. Jellyfin derives the
probe path out of the FFmpeg path it was given by replacing the **whole final filename** with
`ffprobe` and keeping its extension: `…\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe` is probed with
`…\anaglyfin\ffmpeg\ffprobe.exe`, never with `anaglyfin-ffprobe.exe`, and the derived path is looked
for in that directory alone. And because the server starts its FFmpeg by handing the configured path
to the operating system rather than to a shell, that path is used exactly as written: a space in it
is fine, and quote characters around it are part of the name — a configured path wrapped in literal
`"` characters is a file that cannot be found. The wrapper treats the paths in its own variables the
same way, trimming them but never removing quotes.

Jellyfin finds `ffprobe` **beside the FFmpeg path it was given** — beside the wrapper — never on `PATH`.
The wrapper is what the server's FFmpeg path points at, so the official `ffprobe` goes into the same
directory as the wrapper and `ffmpeg-mvc`.
The three server shapes differ only in who fills that directory:

- **Docker:** a series of `docker compose exec` commands against the running container compiles
  FFmpeg-mvc and publishes the wrapper into `/config/anaglyfin/ffmpeg`, linking the two FFmpeg
  binaries onto the FFmpeg runtime libraries the official image already ships. The Docker host places
  nothing, and the container needs no extra packages to run what the commands produce.
- **Linux bare metal:** you install all three yourself (§4.1) — the wrapper from the release asset,
  the `ffmpeg-mvc` you built, the stock `ffprobe` off the package's own FFmpeg.
- **Windows server:** you install all three yourself too (§4.2), and two of the three you built
  first, because nothing publishes a Windows binary.

## 3. Docker server (recommended)

Every command below runs **inside the running container**. Nothing is installed on the Docker host
and nothing extra is mounted for the runtime. Assumes the service is named `jellyfin`, the image is
`jellyfin/jellyfin:12.4`, and `./jellyfin/config` is mounted at `/config`. Every command below
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
  https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.3-mvc10-jf1.tar.gz \
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

**6. The wrapper: fetch and unpack.** `v0.2.2` is a tag, not a branch — the source arrives as a tag
archive.

```sh
docker compose exec -u root jellyfin curl -fsSL \
  https://github.com/tthayer93/Anaglyfin/archive/refs/tags/v0.2.2.tar.gz \
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
`jellyfin/jellyfin:12.4`:

```sh
docker compose up -d --force-recreate jellyfin
```

That is the stock container again: its own FFmpeg, the runtime files gone with `/config/anaglyfin`,
and any build packages from step 1 discarded with the container they were installed into — they were
never part of the runtime, so there is nothing else to undo. Take the plugin out of Dashboard →
Plugins too if it should go with it.

## 4. Bare-metal server

**Choose your platform first.** Both shapes below start from a server where Jellyfin's own package or
service is **already installed and running** — the Debian/Ubuntu package, or the `JellyfinServer`
service from the official Windows installer — and neither shape installs it. What this section does
on both is install the same three runtime files (§2) and name the same five variables to the server;
what differs is where the files live, who is allowed to write them, and how a service is handed an
environment at all.

| Server shape | The service | Runtime files live in | The five variables go in | § |
| --- | --- | --- | --- | --- |
| Debian/Ubuntu package, already installed | `jellyfin`, a systemd unit | `/opt/anaglyfin/ffmpeg`, with `lock` and `wrapper` under `/var/lib/jellyfin/anaglyfin` | a systemd drop-in of `Environment=` lines | §4.1 |
| Official Windows installer, already installed | `JellyfinServer`, hosted by the NSSM the installer ships | `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg`, with `lock` and `wrapper` beside it | the service's `AppEnvironmentExtra` registry value (a `REG_MULTI_SZ` of `NAME=VALUE` lines) | §4.2 |

If the server is a container, this section is the wrong one — §3 fills the same three slots from
inside the running image. If Jellyfin is not installed and running yet on either platform, that is a
job for Jellyfin's own documentation before anything here.

### 4.1 Linux server — Debian/Ubuntu package already installed

Assumes the `jellyfin` package is installed and its service running; this section hands Anaglyfin's
runtime files to that service, it does not install the package. Same grouping under
`/opt/anaglyfin/ffmpeg`; `lock` and `wrapper` under the server data directory, everything owned by
the `jellyfin` user:

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

### 4.2 Windows server — Jellyfin already installed

These steps assume the official Windows installer has **already run** here and its `JellyfinServer` service
starts. **This section does not install Jellyfin:** if `Get-Service JellyfinServer` finds nothing, install
and configure the server through [Jellyfin's own documentation](https://jellyfin.org/docs/) first — a
portable zip or a hand-run `jellyfin.exe` is not the shape anything here configures. What it does is §4.1 on
Windows: the same three runtime files (§2) and the same five variables under
`C:\ProgramData\Jellyfin\Server`, handed to the service instead of to a systemd drop-in, with the plugin of
§1 coming from the same catalog. Everything below is PowerShell (5.1 or 7) **elevated to Administrator** —
steps 2 and 6–7 touch ACLs, `HKLM` and the service — except step 4, which runs in MSYS2 CLANG64.

**Status: documented, but not runtime-validated by this project.** Every runtime result recorded in
`docs/validation.md` was observed on Linux, inside `jellyfin/jellyfin:12.2` and on the Debian/Ubuntu
package; neither the wrapper nor FFmpeg-mvc has ever executed on Windows in this project's CI or validation
runs, so this route is documentation and not evidence. The wrapper is *written* for Windows — an
`ffmpeg.exe` candidate on `PATH`, a case-insensitive self-comparison so it cannot launch itself, no POSIX
signal registration — but Linux CI never takes those branches, and no runtime result on any Windows host
exists for any of them. Two facts force local builds: **there is no `win-x64` wrapper release asset**, so
step 3 publishes the same tag for `win-x64`; and **FFmpeg-mvc publishes no binaries for any platform**, so
step 4 builds one. Nobody has shown the whole thing working, so step 8 is part of the install rather than a
formality: there, your own server is the evidence.

**1. Prerequisites** — check these before building anything.

```powershell
Get-Service JellyfinServer
Get-ChildItem "C:\Program Files\Jellyfin\Server\ffmpeg.exe",
              "C:\Program Files\Jellyfin\Server\ffprobe.exe"
git --version
dotnet --version
```

- **`JellyfinServer` exists and is running.** Only the official installer creates that service, points it at
  `C:\ProgramData\Jellyfin\Server` and registers it with the bundled NSSM; absent, Jellyfin's own
  documentation is the next step, not this page.
- **Both stock FFmpeg files are present:** `ffmpeg.exe` runs ordinary playback and the capability probes
  (`ANAGLYFIN_SERVER_FFMPEG`) and `ffprobe.exe` is one of the three runtime files. Get a missing one from
  Jellyfin — never point `ANAGLYFIN_SERVER_FFMPEG` at the FFmpeg-mvc build instead.
- **Git, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not the runtime) and
  MSYS2 with its [CLANG64](https://www.msys2.org/) environment** serve steps 3 and 4, and all three are
  build-time only: the wrapper publishes self-contained, so the server needs no .NET runtime to run it.

**2. Directories, and the right to write them** — one directory for the three files, two for what
the server writes into, one grant covering all three.

```powershell
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"
$ffmpegDir = "$anaglyfinRoot\ffmpeg"
$lockDir = "$anaglyfinRoot\lock"
$wrapperDir = "$anaglyfinRoot\wrapper"

New-Item -ItemType Directory -Force -Path `
  $ffmpegDir, $lockDir, $wrapperDir | Out-Null

icacls $anaglyfinRoot /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)M" /T
```

`ffmpeg` takes the three runtime files; `lock` and `wrapper` are written **while the service runs** — the
wrapper's concurrency slots and the plugin's settings document — which is what the grant is for: `M` with
`(OI)(CI)` so new files inherit it and `/T` to push it onto anything already in the tree. The installer runs
`JellyfinServer` as **Network Service**, the account named above; if this service got a different one, grant
to that account instead of guessing — read it with
`Get-CimInstance Win32_Service -Filter "Name='JellyfinServer'" | Select-Object -ExpandProperty StartName`.

**3. Build the Anaglyfin wrapper for `win-x64`** — there is no Windows release asset to download, so
build the tag.

```powershell
git clone --depth 1 --branch v0.2.2 `
  https://github.com/tthayer93/Anaglyfin.git C:\build\anaglyfin-wrapper-src
cd C:\build\anaglyfin-wrapper-src

dotnet publish .\src\Anaglyfin.FFmpegWrapper\Anaglyfin.FFmpegWrapper.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=embedded `
  --output C:\build\anaglyfin-wrapper

Rename-Item C:\build\anaglyfin-wrapper\Anaglyfin.FFmpegWrapper.exe anaglyfin-ffmpeg.exe
```

Clone the **tag** the server has installed — `v0.2.2` is what §1's catalog installs, and a release bumps
the plugin and the wrapper together — not a branch tip. Git and the SDK are build-time tools;
`--self-contained` is what keeps the server free of a .NET runtime. Rename **only**
`Anaglyfin.FFmpegWrapper.exe` → `anaglyfin-ffmpeg.exe`: the apphost knows its own managed entry point's
location from build time rather than from its filename, so Jellyfin may see any name, whereas a publish
without single-file leaves `Anaglyfin.FFmpegWrapper.dll` and its `.runtimeconfig.json` beside the exe and
the apphost loads those **by name** — never rename them, and if the output above is a DLL pair rather than
one exe, the single-file switch did not apply.

**4. Build FFmpeg-mvc** *(MSYS2 CLANG64; advanced; not run by this project)*

FFmpeg-mvc publishes no binaries for any platform (§2) and carries no Windows build scripts, so this half
of the runtime is a local build on the parent project's route: `jellyfin-ffmpeg` produces its own official
Windows binaries on MSYS2 with the CLANG64 (`mingw-w64-clang-x86_64`) toolchain, and its `msys2/` scripts
are copied in beside the FFmpeg-mvc source and run there. Open **MSYS2 CLANG64** — *not* the plain `MSYS`
shell and *not* `MINGW64`: the toolchain installs into CLANG64 and the build cannot see it from the other
two. The two tags below belong together (`n8.1.3-mvc10-jf1` drops in against `jellyfin-ffmpeg v8.1.3-1`) and
the parent checkout supplies only its `msys2/` scripts, never an installed binary; `/c/build/anaglyfin` is
scratch space for this build alone.

```bash
pacman -Syu --noconfirm
pacman -S --needed --noconfirm \
  git curl wget zip unzip \
  quilt diffstat \
  mingw-w64-clang-x86_64-toolchain \
  mingw-w64-clang-x86_64-nasm

mkdir -p /c/build/anaglyfin
cd /c/build/anaglyfin
git clone --depth 1 --branch v8.1.3-1 https://github.com/jellyfin/jellyfin-ffmpeg.git
git clone --depth 1 --branch n8.1.3-mvc10-jf1 https://github.com/tthayer93/FFmpeg-mvc.git
cp -r jellyfin-ffmpeg/msys2 FFmpeg-mvc/
cd FFmpeg-mvc
bash msys2/build.sh
```

**The artifact is the source-root `/c/build/anaglyfin/FFmpeg-mvc/ffmpeg.exe`** —
`C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe` in step 5's spelling — because `build.sh` configures and compiles
in the source tree, so that file exists the moment `make` finishes. Everything the script does *after*
`make` is the parent's packaging: it reads `debian/changelog` to name the portable zip and then *moves*
`ffmpeg.exe` and `ffprobe.exe` into its own `msys2/` directory, and an FFmpeg-mvc product tag has no
`debian/changelog` at all — so the script can exit non-zero on that read **after a build that succeeded**.
That is a packaging mismatch, not a compiler failure, arguing about a binary that is already built and
still in the source root; only a script that ran to its end has moved it, to
`C:\build\anaglyfin\FFmpeg-mvc\msys2\ffmpeg.exe`. Step 5 installs the source-root **`ffmpeg.exe`** as
`ffmpeg-mvc.exe` and leaves the `ffprobe.exe` beside it behind: the probe beside the wrapper is the
server's own (§2), always.

This project has never run this build, so if it fails, do not invent `configure` flags — start again from
the parent's published `msys2/build.sh` and its dependencies, or take the question to the FFmpeg-mvc
maintainer, because a hand-rolled flag set is how you get a binary that advertises an encoder it cannot run.

**5. Copy the three runtime files** — one copy of each into the directory from step 2.

```powershell
$ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"

# the wrapper, as built and renamed by step 3
Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe `
  "$ffmpegDir\anaglyfin-ffmpeg.exe" -Force

# FFmpeg-mvc's own ffmpeg.exe, from the build's source root, under the name this page
# uses; only if step 4's packaging ran to its end did it move under msys2\ instead.
Copy-Item C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe `
  "$ffmpegDir\ffmpeg-mvc.exe" -Force

# the STOCK ffprobe, from the installer — not the one step 4 produced
Copy-Item "C:\Program Files\Jellyfin\Server\ffprobe.exe" `
  "$ffmpegDir\ffprobe.exe" -Force
```

`Get-ChildItem $ffmpegDir` now shows exactly those three `.exe` files, the wrapper one tens of megabytes
across because it is self-contained. Files copied into step 2's directories inherit the modify right the
service needs there.

**6. Service environment — the service's own, not your shell's.**

Jellyfin hands a started helper the environment **the service** was started with, so the five variables
belong there: your shell's variables reach only your session and `setx` writes the boot-time environment, so
neither reaches a service that is already running. NSSM — how the installer manages `JellyfinServer` — keeps
what it launches the app with under the service's own registry key, and this block inspects that value, keeps
whatever is neither empty nor one of the names it states again, adds the five, and writes the union:

```powershell
$parameters = "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters"
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"
$ffmpegDir = "$anaglyfinRoot\ffmpeg"
$anaglyfinEnvironment = @(
  "JELLYFIN_FFMPEG=$ffmpegDir\anaglyfin-ffmpeg.exe"
  "ANAGLYFIN_REAL_FFMPEG=$ffmpegDir\ffmpeg-mvc.exe"
  "ANAGLYFIN_SERVER_FFMPEG=C:\Program Files\Jellyfin\Server\ffmpeg.exe"
  "ANAGLYFIN_LOCK_DIR=$anaglyfinRoot\lock"
  "ANAGLYFIN_WRAPPER_SETTINGS=$anaglyfinRoot\wrapper\anaglyfin-wrapper-settings.json"
)
# Inspect first: on a server never configured this way AppEnvironmentExtra does not exist
# yet, the read reports nothing, and $kept is empty.
$existing = @(
  (Get-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
     -ErrorAction SilentlyContinue).AppEnvironmentExtra
)
$kept = @($existing | Where-Object { $_ -and $_ -notmatch '^(JELLYFIN_FFMPEG=|ANAGLYFIN_)' })
# Write the union back as REG_MULTI_SZ, one NAME=VALUE per entry.
Set-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
  -Value ($kept + $anaglyfinEnvironment) -Type MultiString
```

Three rules about this value, each of them load-bearing:

- **`AppEnvironmentExtra`, never `AppEnvironment`:** the value above is *appended* to the environment the
  service starts with, while `AppEnvironment` **replaces** that environment — the five variables written
  into it would leave Jellyfin with no `PATH`, no `SystemRoot`, and nothing else it needs to start.
- **`REG_MULTI_SZ`, one `NAME=VALUE` per entry:** `-Type MultiString` writes exactly the shape the bundled
  `nssm.exe set JellyfinServer AppEnvironmentExtra NAME=VALUE …` writes, so either tool is fine and
  neither is a second configuration to keep in sync. Never pack several assignments into one entry.
- **No quotes around a path:** these values reach the process as written, and Jellyfin starts its FFmpeg by
  handing the path to the operating system rather than to a shell. `ANAGLYFIN_SERVER_FFMPEG` above points
  into `C:\Program Files\...` and is correct exactly as it stands; that path wrapped in literal `"`
  characters names a file that cannot be found, and the log then reports a failed version check or a path
  that is invalid.

These are the five of §3 step 9, and `ANAGLYFIN_WRAPPER_SETTINGS` is not one to skip: the plugin writes its
subtitle-depth and concurrency document to exactly that path and a wrapper can only read a document it is
*told* about, so unset it the file sits under `plugins\configurations` where no wrapper looks and subtitle
depth quietly stays flat with nothing logged as an error.

**7. Restart the service** — a restart is the whole of the wait:

```powershell
Restart-Service JellyfinServer
```

NSSM reads `Parameters` when it launches the server, so the new environment belongs only to processes
started after this command — and this is also the restart that loads a plugin installed through §1.

**8. Verify** — because this route is documented and not validated, this step is the evidence. The log
directory for this shape is `C:\ProgramData\Jellyfin\Server\log`.

- **The startup log names the Windows wrapper**, on the line §5 quotes for every shape:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe
  ```

  The startup environment dump echoes only `JELLYFIN_`, `DOTNET_` and `ASPNETCORE_` names, so it proves
  `JELLYFIN_FFMPEG` and not the `ANAGLYFIN_*` entries — the commands below are their proof.

- **The stock `ffprobe.exe` sits beside the wrapper.** Two matching first lines are the answer wanted, since
  an FFmpeg-mvc `ffprobe` names the mvc tag in its banner. A missing sidecar shows up at startup as a probe
  failure naming `…\anaglyfin\ffmpeg\ffprobe.exe` and a library that never fills.
- **The wrapper dispatches to FFmpeg-mvc.** With `ANAGLYFIN_SERVER_FFMPEG` unset for one invocation (a blank
  value counts as unset) an ordinary `-version` takes the marker route, so the banner that comes back is
  FFmpeg-mvc's — the check §5 runs with `env`. It runs as **your** account, so it proves dispatch and the
  binaries and not the service account's rights.
- **One real Anaglyfin transcode plays**, from a client, with an MVC item and a profile enabled. It leaves a
  `transcode_<n>.log` in that log directory; the wrapper's command line carries the marker URL and its
  `ffmpeg-mvc.exe` child's carries the real source path and the profile's filters with no marker in it
  (`docs/validation.md` V6/V7). With hardware encoding on, the encoder Jellyfin selects for an Anaglyfin job
  (read `-codec:v` off that log) also has to be in `ffmpeg-mvc.exe -encoders`, as §3 step 9 asks.

  The three command checks above, in one block — the last of them only while a transcode is running:

  ```powershell
  $ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"
  & "$ffmpegDir\ffprobe.exe" -version | Select-Object -First 1   # must match the line below
  & "C:\Program Files\Jellyfin\Server\ffprobe.exe" -version | Select-Object -First 1
  $env:ANAGLYFIN_REAL_FFMPEG = "$ffmpegDir\ffmpeg-mvc.exe"
  Remove-Item Env:ANAGLYFIN_SERVER_FFMPEG -ErrorAction SilentlyContinue
  & "$ffmpegDir\anaglyfin-ffmpeg.exe" -version                   # banner must name FFmpeg-mvc
  Get-CimInstance Win32_Process -Filter "Name='anaglyfin-ffmpeg.exe' OR Name='ffmpeg-mvc.exe'" |
    Select-Object ProcessId, Name, CommandLine
  ```

- **Stopping is the caveat specific to Windows.** There are no POSIX signals there and the wrapper registers
  no stop-signal handler at all, so a stop rests entirely on the `q` the server writes to FFmpeg's stdin,
  which reaches the FFmpeg-mvc child through the stdin the wrapper inherits. When a transcode ignores that
  `q`, the server waits five seconds and terminates the process it owns — the **wrapper** — and Windows kills
  no process tree with that call, so an FFmpeg-mvc child can be left orphaned behind a restarted service.
  `Get-Process ffmpeg-mvc` after a stopped playback tells you, and `Stop-Process` by that name or another
  service restart clears it. The concurrency slot does not go with the orphan: a slot file is opened
  `DeleteOnClose`, so the OS releases it the moment the wrapper is gone and the next Anaglyfin playback is not
  locked out. A stop that hangs *is* worth writing up, because no run of this project has measured the `q`
  path on Windows.

`docs/validation.md` keeps the full checklist, and its rows are Linux results: a Windows run that passes
them is new evidence and should be written up as such rather than assumed to have been shown already.

**9. Upgrade and uninstall.**

Both halves install over themselves; the one Windows-specific wrinkle is that a running `.exe` cannot be
overwritten, so a file is swapped with the service stopped.

- **The wrapper** — the tag changes, the clone does not: `git clone` refuses an existing directory, so move
  step 3's checkout onto the new tag (`--depth 1` keeps it shallow, `--detach` lands on it from the detached
  HEAD the old tag left):

  ```powershell
  cd C:\build\anaglyfin-wrapper-src
  git fetch --depth 1 origin "<new-tag>"
  git checkout --detach FETCH_HEAD
  ```

  Then re-run step 3's publish block unchanged, rename its output as step 3 does, and swap the file with the
  service stopped:

  ```powershell
  Stop-Service JellyfinServer
  Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe `
    "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -Force
  Start-Service JellyfinServer
  ```

- **FFmpeg-mvc** — the same two git commands in its clone at `C:\build\anaglyfin\FFmpeg-mvc`, rebuild, and
  swap `ffmpeg-mvc.exe` the same way. The copied `msys2/` belongs to neither tag, so the checkout leaves it
  where it is. Do **not** replace `ffprobe.exe` from that build (§2).
- **The plugin** — from the catalog, then `Restart-Service JellyfinServer`.
- **Uninstall** removes **Anaglyfin's** service-environment entries and its runtime directory. It does **not
  uninstall Jellyfin** and leaves every stock Jellyfin file alone: everything under
  `C:\Program Files\Jellyfin\Server` — the server, its `ffmpeg.exe` and `ffprobe.exe`, its NSSM — stays as
  Jellyfin installed it, and the plugin goes only when you take it out of Dashboard → Plugins. As in §3.3,
  **every** Anaglyfin entry has to leave `AppEnvironmentExtra`: delete the value if it carried nothing else,
  or write it back by hand with only the non-Anaglyfin entries (step 6's filter matches `JELLYFIN_FFMPEG`
  and `ANAGLYFIN_` by name, so it leaves anything else in place).

  ```powershell
  Remove-ItemProperty `
    -Path "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters" `
    -Name AppEnvironmentExtra
  Remove-Item -Recurse -Force "C:\ProgramData\Jellyfin\Server\anaglyfin"
  Restart-Service JellyfinServer
  ```

  `JELLYFIN_FFMPEG` left naming the wrapper that was just deleted is what bites: the server looks for its
  FFmpeg where the wrapper used to be. With the value gone it is back to the FFmpeg in its own program
  directory — unless a path was saved in **Dashboard → Playback → FFmpeg path**, which outranks the default
  and is where the same correction belongs. Step 2's grant goes with the directory it was applied to.

## 5. Verify and update

These are the checks every shape above ends on; §4.2 step 8 adds the ones specific to a Windows
service on top of them.

- `Dashboard → Plugins` lists **Anaglyfin** and its settings page opens.
- The server log names the wrapper as Jellyfin's FFmpeg. The `MediaEncoder: FFmpeg:` line, naming the
  wrapper's path, is what identifies the wrapper as Jellyfin's FFmpeg; no server-reported FFmpeg
  version is part of the contract. Docker:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

  Linux bare metal:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ```

  Windows server:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe
  ```

- The wrapper answers `-version` straight through to a real encoder. `-version` is an ordinary
command, so it is dispatched to `ANAGLYFIN_SERVER_FFMPEG` when that variable is set. To check that
the **FFmpeg-mvc** binary is reachable, the snippets below explicitly blank
`ANAGLYFIN_SERVER_FFMPEG` (a blank value counts as unset), which sends the ordinary command down the
real/marker route so the banner is the FFmpeg-mvc build's. Docker:

  ```sh
  docker compose exec -u root jellyfin \
    env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/config/anaglyfin/ffmpeg/ffmpeg-mvc \
    /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
  ```

  Linux bare metal:

  ```sh
  sudo -u jellyfin env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc \
    /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
  ```

  Windows server — the same blanking and the same banner, in PowerShell:

  ```powershell
  $env:ANAGLYFIN_REAL_FFMPEG = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffmpeg-mvc.exe"
  Remove-Item Env:ANAGLYFIN_SERVER_FFMPEG -ErrorAction SilentlyContinue
  & "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -version
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
- **Linux bare metal:** swap the file under `/opt/anaglyfin/ffmpeg/` by hand and restart.
- **Windows server:** repeat the build for the half you are moving (§4.2 step 9), swap the file under
  `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\` with `JellyfinServer` stopped, and start it
  again — Windows will not overwrite the wrapper while a transcode is running it.
