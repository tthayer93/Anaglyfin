# Installing Anaglyfin

Install the plugin from one repository URL, get three FFmpeg runtime files onto the server,
and point Jellyfin's FFmpeg at the wrapper. On Docker the commands in §3 run inside the running
container and build both halves there. Target: Jellyfin **12.x**, on any of the three server
shapes this page documents — the official container `jellyfin/jellyfin:12.2` (§3), which is the
image this flow currently targets; the Debian/Ubuntu package (§4.1); and the official Windows
server installer (§4.2). The plugin's `targetAbi 12.0.0` is a minimum-version floor rather than
an exact match, so `v0.2.2` loads on Jellyfin 12.0 or later.

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
  `jellyfin-8.1` branch; latest tag `n8.1.3-mvc9-jf1`. This is the binary the wrapper dispatches
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
`jellyfin/jellyfin:12.2`, and `./jellyfin/config` is mounted at `/config`. Every command below
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
  https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.3-mvc9-jf1.tar.gz \
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
`jellyfin/jellyfin:12.2`:

```sh
docker compose up -d --force-recreate jellyfin
```

That is the stock container again: its own FFmpeg, the runtime files gone with `/config/anaglyfin`,
and any build packages from step 1 discarded with the container they were installed into — they were
never part of the runtime, so there is nothing else to undo. Take the plugin out of Dashboard →
Plugins too if it should go with it.

## 4. Bare-metal server

**Choose your platform first.** Both shapes below install the same three runtime files (§2) and name
the same five variables to the server; what differs is where the files live, who is allowed to write
them, and how a service is handed an environment at all.

| Server shape | The service | Runtime files live in | The five variables go in | § |
| --- | --- | --- | --- | --- |
| Debian/Ubuntu package | `jellyfin`, a systemd unit | `/opt/anaglyfin/ffmpeg`, with `lock` and `wrapper` under `/var/lib/jellyfin/anaglyfin` | a systemd drop-in of `Environment=` lines | §4.1 |
| Official Windows installer | `JellyfinServer`, hosted by the NSSM the installer ships | `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg`, with `lock` and `wrapper` beside it | the service's `AppEnvironmentExtra` registry value (a `REG_MULTI_SZ` of `NAME=VALUE` lines) | §4.2 |

If the server is a container, this section is the wrong one — §3 fills the same three slots from
inside the running image.

### 4.1 Linux (Debian/Ubuntu package)

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

### 4.2 Windows server (official installer)

This is the same runtime as §4.1 in every respect that matters to the plugin — the same wrapper from
the same tag, an FFmpeg-mvc build of the same branch, the server's own stock `ffprobe`, the same five
variables — arranged the way the official Windows installer arranges a server: everything the service
needs under `C:\ProgramData\Jellyfin\Server`, and the environment handed to the `JellyfinServer`
service itself rather than to a shell. The plugin install of §1 is the same catalog install as
everywhere else; only the fallback directory and the restart command differ.

**Not runtime-validated by this project.** Every runtime result recorded in `docs/validation.md`
was observed on Linux — inside `jellyfin/jellyfin:12.2`, and on the Debian/Ubuntu package. Neither
the wrapper nor FFmpeg-mvc has ever been executed on Windows by this project's CI or by its
validation runs, so what this route carries is documentation, not evidence. State the two halves of
that precisely, because they are not the same claim: the wrapper is **written** for Windows and its
Windows behaviour is pinned by tests that run on the other platforms — it offers the `ffmpeg.exe`
candidate when it searches `PATH`, compares its own path case-insensitively so it cannot be talked
into launching itself, and registers no stop-signal handler at all where the platform has no POSIX
signals to register one for. What nobody has shown is the rest: that this wrapper, in front of a
Windows FFmpeg-mvc build, on a Windows `JellyfinServer`, plays a movie. Two facts also make this the
longest of the three flows, and neither is a documentation gap:

- **there is no `win-x64` wrapper release asset.** The published `anaglyfin-ffmpeg` is a linux-x64
  binary, so step 3 publishes the same tag for `win-x64` on your machine.
- **FFmpeg-mvc publishes no binaries at all** — Windows or otherwise. That is equally true of the
  Linux flows, which compile it; on Windows the build in step 4 is an advanced local build, ported
  from the parent `jellyfin-ffmpeg` project's MSYS2 CLANG64 pipeline, and this project has not run
  it either.

Run it as written, and treat step 8 as part of the install rather than as a formality. Everything
below runs in PowerShell — step 4 alone runs in MSYS2 — in a session **elevated to Administrator**,
because step 2 changes access control under `C:\ProgramData` and steps 6–7 write under `HKLM` and
control a service.

**1. Prerequisites** — check these before building anything.

The server itself is the **official Windows installer**, not the portable zip: the installer is what
creates the `JellyfinServer` service, points it at `C:\ProgramData\Jellyfin\Server` as its data
directory, and registers it with the NSSM binary the installer ships.

```powershell
Get-Service JellyfinServer
```

Its program directory has to carry the stock FFmpeg pair, because both halves of it are used below —
`ffmpeg.exe` is the binary ordinary playback and the server's own capability probes run on
(`ANAGLYFIN_SERVER_FFMPEG`), and `ffprobe.exe` is one of the three runtime files:

```powershell
Get-ChildItem "C:\Program Files\Jellyfin\Server\ffmpeg.exe",
              "C:\Program Files\Jellyfin\Server\ffprobe.exe"
```

If either is missing, this server has no stock FFmpeg to hand ordinary playback to; install one
before continuing rather than pointing `ANAGLYFIN_SERVER_FFMPEG` at the FFmpeg-mvc build and calling
the result a two-binary deployment.

Then what you build with — every one of these is build-time only, and none of what they produce
needs them at run time — plus the shell the rest of this section runs in:

- **Git**, for the two clones below.
- **.NET 10 SDK** — the SDK from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0),
  not just the runtime — for step 3. The wrapper publishes *self-contained*, so nothing about this
  changes what the server itself needs: no .NET runtime is required on the server to run the wrapper
  afterwards.
- **MSYS2** ([msys2.org](https://www.msys2.org/)) for step 4, and with it the **CLANG64** environment —
  the build below is a clang/MinGW build, not a Visual Studio one, and the parent project's own
  official Windows FFmpeg binaries are produced exactly that way.
- **PowerShell**, in the elevated session above: Windows PowerShell 5.1 and PowerShell 7 both run
  every snippet here as written.

**2. Directories, and the right to write them** — one directory for the three files, two for what
the server writes into, and one access-rights grant covering all three.

```powershell
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"
$ffmpegDir = "$anaglyfinRoot\ffmpeg"
$lockDir = "$anaglyfinRoot\lock"
$wrapperDir = "$anaglyfinRoot\wrapper"

New-Item -ItemType Directory -Force -Path `
  $ffmpegDir, $lockDir, $wrapperDir | Out-Null

icacls $anaglyfinRoot /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)M" /T
```

`ffmpeg` holds the three runtime files; `lock` is where a running wrapper creates the file slots
behind the concurrency limit, and `wrapper` holds the settings document the plugin writes for the
wrappers. Those two are written *while the service runs*, which is what the `icacls` line is for:
`M` (modify — create, write, delete; `F` would be more than this needs) with `(OI)(CI)` so files and
directories created inside inherit it, and `/T` to push that onto anything already in the tree.

The installer runs `JellyfinServer` as **Network Service** by default, and that is the account named
above. If the service was given a different account, grant to that account instead of guessing at it
— read it back:

```powershell
Get-CimInstance Win32_Service -Filter "Name='JellyfinServer'" |
  Select-Object -ExpandProperty StartName
```

and use whatever it answers (`NT AUTHORITY\LOCAL SERVICE`, `NT AUTHORITY\SYSTEM`, or a named account)
in place of `NT AUTHORITY\NETWORK SERVICE`.

*Optional:* with `IncludeNativeLibrariesForSelfExtract` the single-file wrapper unpacks its native
runtime into the service account's temp directory on first run (`C:\Windows\Temp` for Network
Service, where the default rights are enough). To put those files somewhere else instead, create a
directory of your own under `$anaglyfinRoot`, give it the same grant step 2 gave the rest of the
tree, and add one more entry to step 6's list naming it:
`DOTNET_BUNDLE_EXTRACT_BASE_DIR=C:\ProgramData\Jellyfin\Server\anaglyfin\bundle`.

**3. The wrapper: build it for `win-x64`** — there is nothing to download, so build the tag.

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

`v0.2.2` is the version this page installs from the catalog in §1, and a release bumps the plugin and
the wrapper together, so the tag to clone is the version the server has installed — use the tag, not a
branch tip. `--self-contained` is what keeps the server free of a .NET runtime, and
`PublishSingleFile` is what leaves one file to install — `anaglyfin-ffmpeg.exe`. Renaming it is safe
and is the only renaming allowed here: the native apphost carries the location of its own managed
entry point from build time rather than deriving it from its filename, so Jellyfin is free to see any
name, and `Environment.ProcessPath` simply reports the new one. The managed
`Anaglyfin.FFmpegWrapper.dll` and `Anaglyfin.FFmpegWrapper.runtimeconfig.json` are a different matter
— a publish **without** `PublishSingleFile` leaves that pair beside the exe and the apphost loads them
*by name*, so those two are never renamed. If the output above looks like a DLL pair rather than one
exe, the single-file switch did not apply; fix that rather than renaming around it.

**4. FFmpeg-mvc: the Windows build** *(advanced; not run by this project)*.

FFmpeg-mvc publishes no binaries, so this half of the runtime is always a build: §3.1 compiles it
inside the container, §4.1 asks you to arrive with one, and here you build it yourself. The route
below is the parent project's own: `jellyfin-ffmpeg` builds its official Windows binaries on MSYS2
with the `mingw-w64-clang-x86_64` toolchain — the CLANG64 environment — and its `msys2/` scripts are
what produces the portable `win64-clang` zips. FFmpeg-mvc carries no Windows build scripts of its
own, so the parent's are copied in beside its source and run against the product tag. **This project
has never run this build**: it is the parent pipeline with FFmpeg-mvc's source in place of the
parent's, ported rather than executed here, and it has no Windows runtime evidence behind it either
(§4.2's opening warning). If it fails, do not invent `configure` flags — start again from the
parent's published `msys2/build.sh` and its dependencies, or take the question to the FFmpeg-mvc
maintainer, because a hand-rolled flag set is how you end up with a binary that advertises an encoder
it cannot run.

Open **MSYS2 CLANG64** — the clang64 environment, *not* the plain `MSYS` shell and *not* `MINGW64`;
the toolchain below installs into CLANG64 and the build cannot see it from the other two.

```bash
pacman -Syu --noconfirm
pacman -S --needed --noconfirm \
  git curl wget zip unzip \
  quilt diffstat \
  mingw-w64-clang-x86_64-toolchain \
  mingw-w64-clang-x86_64-nasm
```

Then the two sources: the parent, for its build scripts, and the FFmpeg-mvc product tag, for the
code. The two tags belong together — `n8.1.3-mvc9-jf1` is the Jellyfin-line FFmpeg-mvc tag that drops
in against `jellyfin-ffmpeg v8.1.3-1` — and the parent tag is only ever the supplier of the `msys2/`
scripts, never a binary that gets installed.

```bash
mkdir -p /c/build/anaglyfin
cd /c/build/anaglyfin
git clone --depth 1 --branch v8.1.3-1 https://github.com/jellyfin/jellyfin-ffmpeg.git
git clone --depth 1 --branch n8.1.3-mvc9-jf1 https://github.com/tthayer93/FFmpeg-mvc.git
cp -r jellyfin-ffmpeg/msys2 FFmpeg-mvc/
```

and build:

```bash
cd FFmpeg-mvc
bash msys2/build.sh
```

`/c/build/anaglyfin` is scratch space for this build alone — the wrapper's own build tree from step 3
is elsewhere (`C:\build\anaglyfin-wrapper-src`), and none of it is needed once step 5 has the files.
`build.sh` configures and builds in the source tree, and then moves `ffmpeg.exe` and `ffprobe.exe`
into its own `msys2/` directory and zips them as the portable package under `msys2/artifacts/` — that
last part is the parent project's packaging, and it reads the parent's `debian/changelog` for the
version in the zip's name, so a product tree without that file can stop the script after a successful
build rather than spoil the build itself. Either way the file wanted is the `ffmpeg.exe` this build
wrote: `C:\build\anaglyfin\FFmpeg-mvc\msys2\ffmpeg.exe` when the script ran to its end, and
`C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe` when it stopped before the move. Step 5 installs that
**`ffmpeg.exe`** as `ffmpeg-mvc.exe` and deliberately leaves the `ffprobe.exe` beside it behind: the
probe beside the wrapper is the server's own (§2), always. The parent's Windows configuration is a
full hardware set (`dxva2`, `d3d11va`, `d3d12va`, `mediafoundation`, `libvpl` for Quick Sync, AMF,
NVENC), so a build through this route should carry more encoders than the documented Linux build
carries — "should" being exactly the strength of that claim, so run the `-encoders` check in step 8
against your own two binaries if the server has hardware encoding switched on. It also enables
`libfdk_aac`, which means the one capability inversion this project has a fix for is live here: on a
server with **Enable audio VBR** on, the stock probe advertises fdk, the server writes that selection
and its private `-vbr` option into a marker command, and the `v0.2.1`-and-later wrapper maps it to
native AAC with a notice (see §3 step 9 for what that log line looks like, and why a wrapper older
than `v0.2.1` dies on it).

**5. The three runtime files** — one copy of each into the directory from step 2.

```powershell
$ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"

# the wrapper, as built and renamed by step 3
Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe `
  "$ffmpegDir\anaglyfin-ffmpeg.exe" -Force

# FFmpeg-mvc, as built by step 4: its ffmpeg.exe, installed under the name this page uses.
# Use whichever of the two paths step 4 describes the build leaving it in.
Copy-Item C:\build\anaglyfin\FFmpeg-mvc\msys2\ffmpeg.exe `
  "$ffmpegDir\ffmpeg-mvc.exe" -Force

# the STOCK ffprobe, from the installer — not the one step 4 produced
Copy-Item "C:\Program Files\Jellyfin\Server\ffprobe.exe" `
  "$ffmpegDir\ffprobe.exe" -Force
```

`Get-ChildItem $ffmpegDir` should now show exactly those three `.exe` files, the wrapper one tens of
megabytes across because it is self-contained. Files created inside the directories of step 2 inherit
the right the service needs; if you replace a file with one copied from a location that carries its
own ACL, `Get-Acl` on it (or a re-run of step 2's `icacls`) is the check.

**6. Environment: the service's own, not your shell's.**

Jellyfin starts the wrapper as a child process, so these five variables must be in the environment of
**the service** — six if you took the optional bundle-directory entry in step 2 — because what Jellyfin
passes to a started helper is what the service itself was started with. A variable set in your own
PowerShell session reaches your session only, and `setx` writes the boot-time environment a service
inherits at machine start — neither is the environment of a service that is already running. The
installer manages `JellyfinServer` through the NSSM it ships, and NSSM keeps the settings it launches
the app with under the service's own registry key. Look before writing:

```powershell
Get-ItemProperty `
  "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters" `
  -Name AppEnvironmentExtra -ErrorAction SilentlyContinue
```

A server that has never been configured this way reports nothing: `AppEnvironmentExtra` does not
exist until something creates it. With nothing there, write the five:

```powershell
$ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"

$anaglyfinEnvironment = @(
  "JELLYFIN_FFMPEG=$ffmpegDir\anaglyfin-ffmpeg.exe"
  "ANAGLYFIN_REAL_FFMPEG=$ffmpegDir\ffmpeg-mvc.exe"
  "ANAGLYFIN_SERVER_FFMPEG=C:\Program Files\Jellyfin\Server\ffmpeg.exe"
  "ANAGLYFIN_LOCK_DIR=$anaglyfinRoot\lock"
  "ANAGLYFIN_WRAPPER_SETTINGS=$anaglyfinRoot\wrapper\anaglyfin-wrapper-settings.json"
)

Set-ItemProperty `
  -Path "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters" `
  -Name AppEnvironmentExtra `
  -Value $anaglyfinEnvironment `
  -Type MultiString
```

If that first command came back with entries already in it — a proxy setting, a `JELLYFIN_DATA_DIR`,
anything somebody put there before Anaglyfin — **merge instead of replacing**: keep what is not
Anaglyfin's, and write the union. (This block assumes `$anaglyfinEnvironment` from the block above is
still in the session; re-run that one first if it is not.)

```powershell
$parameters = "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters"

$existing = @(
  (Get-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
     -ErrorAction SilentlyContinue).AppEnvironmentExtra
)

# What is already there, minus empty entries and the names being stated again.
$kept = @($existing | Where-Object { $_ -and $_ -notmatch '^(JELLYFIN_FFMPEG=|ANAGLYFIN_)' })

Set-ItemProperty `
  -Path $parameters `
  -Name AppEnvironmentExtra `
  -Value ($kept + $anaglyfinEnvironment) `
  -Type MultiString
```

Three rules about this value, each of them load-bearing:

- **`AppEnvironmentExtra`, never `AppEnvironment`.** The one written above is *appended* to the
  environment the service starts with. `AppEnvironment` replaces the entire environment of the
  process — writing the five variables into it would leave Jellyfin with no `PATH`, no `SystemRoot`
  and nothing else it needs to start.
- **`REG_MULTI_SZ`, one `NAME=VALUE` per entry.** `-Type MultiString` is what writes that shape, and
  it is the same value the bundled `nssm.exe` writes when you use
  `nssm set JellyfinServer AppEnvironmentExtra NAME=VALUE …`, so either tool is fine and neither is
  a second configuration to keep in sync. Do not pack several assignments into one entry.
- **No quotes around a path.** These values go to the process as they stand, and Jellyfin starts its
  FFmpeg by handing the path straight to the operating system rather than through a shell. A path
  containing a space (`C:\Program Files\Jellyfin\Server\ffmpeg.exe`, above) is correct exactly as
  written; a path wrapped in literal `"` characters names a file that cannot be found, and the
  server's answer is a version-check failure or "path … is invalid" in the log.

`ANAGLYFIN_WRAPPER_SETTINGS` is in that list because it is not optional in practice: the plugin writes
its subtitle-depth and concurrency document to the absolute path this variable names, and a wrapper
can only read that document from a path it is *told*. Unset, the plugin falls back to writing it
under its own `plugins\configurations` directory, where no wrapper has any way of knowing to look, and
subtitle depth quietly stays flat with nothing logged as an error. The rest of the five are as in
§3 step 9 and `docs/architecture.md`: `JELLYFIN_FFMPEG` is the server's FFmpeg path pointed at the
wrapper; `ANAGLYFIN_REAL_FFMPEG` is the FFmpeg-mvc build that runs marker/profile jobs;
`ANAGLYFIN_SERVER_FFMPEG` is the stock FFmpeg that runs everything else, including the startup
capability probes, so that what the server believes about its FFmpeg is true of the binary those
commands run on; `ANAGLYFIN_LOCK_DIR` is where the concurrency slots live.

**7. Restart the service** — and a restart is the whole of the wait:

```powershell
Restart-Service JellyfinServer
```

NSSM reads `Parameters` when it launches the server, so the new environment belongs to processes
started after this command — a client reconnect, or a plugin reload, carries no new environment with
it. This is also the restart that loads a plugin installed or updated through §1.

**8. Verify** — because this route is documented and not validated, this step is the evidence.

- **The log names the wrapper.** The line that identifies the wrapper as the server's FFmpeg is the
  same one §5 quotes for the other shapes, and the server's log directory for this install shape is
  `C:\ProgramData\Jellyfin\Server\log`:

  ```text
  MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe
  ```

  The startup block that lists environment variables echoes only the server's own prefixes —
  `JELLYFIN_`, `DOTNET_` and `ASPNETCORE_` — so `JELLYFIN_FFMPEG` naming the wrapper is visible
  there, while the `ANAGLYFIN_*` names are not. The checks below, and not that line, are the proof of
  them.

- **`ffprobe.exe` is beside the wrapper, and it is the stock one.** The server derives its probe path
  from the FFmpeg path above, so a missing sidecar is visible at startup as a probe failure naming
  `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffprobe.exe`, and a library that never fills. That
  it is the *official* build is the part a copy-paste gets wrong: the banner tells you which one is
  there, because the FFmpeg-mvc build's banner names the mvc tag.

  ```powershell
  & "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffprobe.exe" -version | Select-Object -First 1
  & "C:\Program Files\Jellyfin\Server\ffprobe.exe" -version | Select-Object -First 1
  ```

  Two matching first lines is the answer wanted. Saving the path in **Dashboard → Playback → FFmpeg
  path** is a second way to have the server prove it: that control probes the path it is given and
  refuses it unless the `ffprobe.exe` beside it runs.

- **The wrapper answers `-version`, and dispatches.** Blanking `ANAGLYFIN_SERVER_FFMPEG` for one
  invocation (a blank value counts as unset) sends this ordinary command down the marker route, so the
  banner that comes back is FFmpeg-mvc's — the same check §5 runs with `env`:

  ```powershell
  $ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"
  $env:ANAGLYFIN_REAL_FFMPEG = "$ffmpegDir\ffmpeg-mvc.exe"
  Remove-Item Env:ANAGLYFIN_SERVER_FFMPEG -ErrorAction SilentlyContinue
  & "$ffmpegDir\anaglyfin-ffmpeg.exe" -version
  ```

  Note what this does and does not prove: it runs the wrapper as **your** account, so it tests
  dispatch and the binaries, not the service account's rights — those are step 2's grant and the
  working transcode below.
- **The two binaries' encoder lists**, if the server has hardware encoding switched on: the same diff
  §3 step 9 asks for, since the probes the server reads are the stock binary's while marker jobs run
  on the other. The two lists are not expected to match — they are different builds — so what is being
  looked for is one-directional: the encoder Jellyfin actually selects for an Anaglyfin job (read it
  off the transcode log's `-codec:v`) has to be in the **FFmpeg-mvc** list.

  ```powershell
  & "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffmpeg-mvc.exe" -hide_banner -encoders > "$env:TEMP\mvc-encoders.txt"
  & "C:\Program Files\Jellyfin\Server\ffmpeg.exe" -hide_banner -encoders > "$env:TEMP\stock-encoders.txt"
  Compare-Object (Get-Content "$env:TEMP\mvc-encoders.txt") (Get-Content "$env:TEMP\stock-encoders.txt")
  Select-String -Path "$env:TEMP\mvc-encoders.txt" -Pattern "h264_qsv", "h264_nvenc", "h264_amf", "libx264"
  ```

- **One real Anaglyfin transcode**, from a client, with an MVC item and a profile enabled: the job
  appears as a `transcode_<n>.log` in `C:\ProgramData\Jellyfin\Server\log`, and while it runs the
  process tree is visible in PowerShell rather than in `ps`:

  ```powershell
  Get-CimInstance Win32_Process -Filter "Name='anaglyfin-ffmpeg.exe'" |
    Select-Object ProcessId, CommandLine
  Get-CimInstance Win32_Process -Filter "Name='ffmpeg-mvc.exe'" |
    Select-Object ProcessId, CommandLine
  ```

  The wrapper's command line carries the marker URL; the child's carries the real source path and the
  profile's filters, and no marker (see `docs/validation.md` V6/V7 for what those commands should
  contain).
- **Stopping a transcode — the caveat that is specific to Windows.** There are no POSIX signals on
  Windows, and the wrapper knows it: it registers no stop-signal handler here at all, so a stop that
  would have been forwarded on Linux rests entirely on the path the server already uses to stop any
  transcode — writing `q` to FFmpeg's stdin, which reaches the FFmpeg-mvc child through the stdin the
  wrapper inherits. When that works, the encoder finishes its segment, the wrapper relays its exit
  code, and the slot is released. When it does not — a transcode that ignores the `q` — the server
  waits five seconds and then terminates the process it owns, and the process it owns is the
  **wrapper**: Windows does not kill a process tree with that call, so the FFmpeg-mvc child can be
  left to finish its job on its own, and a `Restart-Service JellyfinServer` taken while that is
  happening can leave it running behind the new one. `Get-Process ffmpeg-mvc` after a stopped playback
  is how to tell. This is a caveat and not a blocker: the concurrency slot does not go with the
  orphan, because a slot file is opened `DeleteOnClose` and the operating system releases it the
  moment the wrapper process is gone, so the next Anaglyfin playback is not locked out by a leftover
  encoder. If the leftover itself bothers you, `Stop-Process -Name ffmpeg-mvc` is the blunt fix — and
  the observation that a stop hangs *is* worth recording, because no run of this project has measured
  the `q` path on Windows.

The full checklist stays where it is: `docs/validation.md`. Its rows are Linux results, so a Windows
run that passes them is new evidence and should be written up as such rather than assumed to have
been shown already.

**9. Upgrade and uninstall.**

Both halves install over themselves, and neither records anything the other depends on; the one
Windows-specific wrinkle is that a running `.exe` cannot be overwritten, so a replacement is done with
the service stopped.

- **The wrapper** — re-run step 3 with the new tag in the clone, then, with the service stopped,
  replace the file and start it again:

  ```powershell
  Stop-Service JellyfinServer
  Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe `
    "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -Force
  Start-Service JellyfinServer
  ```

- **FFmpeg-mvc** — same tag change in the step 4 clone, rebuild, and swap `ffmpeg-mvc.exe` the same
  way. Do **not** replace `ffprobe.exe` from that build: the file beside the wrapper stays the
  installer's stock one (§2).
- **The plugin** — from the catalog, then `Restart-Service JellyfinServer`.
- **Uninstall** is §3.3's rule in Windows shape, and the same trap: take **every** Anaglyfin entry out
  of `AppEnvironmentExtra`, not just some. If the value carried nothing but Anaglyfin's entries, delete
  the value; if it carried anything else, re-run step 6's merge and write back only what was not
  Anaglyfin's — by hand this time, because step 6's filter matches `JELLYFIN_FFMPEG` and `ANAGLYFIN_*`
  by name and so leaves a step 2 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` where it is.

  ```powershell
  Remove-ItemProperty `
    -Path "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters" `
    -Name AppEnvironmentExtra
  Remove-Item -Recurse -Force "C:\ProgramData\Jellyfin\Server\anaglyfin"
  Restart-Service JellyfinServer
  ```

  `JELLYFIN_FFMPEG` left naming the wrapper that was just deleted is what bites: the server looks for
  its FFmpeg where the wrapper used to be. With the value gone it is back to the FFmpeg in its own
  program directory — unless a path was ever saved in **Dashboard → Playback → FFmpeg path**, because
  that saved value outranks the default and is where the same correction belongs. Take the plugin out
  of Dashboard → Plugins too if it should go with it, and leave step 2's grant behind with the
  directory it was applied to.

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
