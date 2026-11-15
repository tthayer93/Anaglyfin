# Installing Anaglyfin

> **Scope:** this page installs Anaglyfin — the plugin plus its three FFmpeg runtime files — on a server where
> **Jellyfin 12 is already installed and running**. It is not a guide to installing Windows, Debian/Ubuntu, Docker, or
> Jellyfin itself; do those through their own documentation and come back with a server that runs.

Every command below does one of two things: puts a file where the server can read it, or names that file to the service.
Nothing here installs Jellyfin, replaces the FFmpeg it ships, or touches its database.

## 1. Scope and prerequisites

| Item | Current value |
| --- | --- |
| Anaglyfin | `v0.2.4`. Plugin and wrapper are published under one tag: they are a pair, and an update moves them together |
| Jellyfin | 12.x; the targeted container image is `jellyfin/jellyfin:12.4`. The manifest's `targetAbi 12.0.0` is a minimum-version floor rather than an exact match, so `v0.2.4` loads on 12.0 or later. Jellyfin 12.4 moved its own .NET baseline to `10.0.14`; Anaglyfin targets `net10.0` inside that band, so nothing is retargeted |
| FFmpeg-mvc | product tag `n8.1.3-mvc11-jf2` on the `jellyfin-8.1` branch — the current Jellyfin-line tag, and the drop-in companion to the parent `jellyfin-ffmpeg v8.1.3-2`. The `n9.0.2-mvc11`/`n9` line carries no `-jf` parent, is not a Jellyfin product line, and is **not** adopted. Jellyfin's stable target remains 12.4. This `mvc11-jf2` pairing **is** runtime-validated for the MVC-to-top-and-bottom flow: the 2026-11-14 entry in `docs/validation.md` records a real MVC file decoded by this tag and delivered as both full- and half-height top-and-bottom by a `jellyfin/jellyfin:12.4` server. What that entry does not cover stays open in that file — the anaglyph profile, hardware (QSV/VA-API) encode, subtitle depth on hardware, and the 3D-client rows. Earlier `n8.1.3-mvc10-jf1`/`v8.1.3-1` and older evidence stays historical. **FFmpeg-mvc publishes no binaries for any platform**, so every `ffmpeg-mvc` below is one you build with the commands given here |
| Wrapper release asset | `anaglyfin-ffmpeg`, a self-contained **linux-x64** binary, published with `SHA256SUMS.txt` in the same release. There is **no** `win-x64` asset. The catalog never delivers this file: installing or updating the plugin moves only the DLL, so the wrapper is placed by the platform section and has to carry **the plugin's own version** — a plugin at one tag over a wrapper from another is the failure §8's wrapper entry covers |

**Before you start.** Jellyfin 12 running as one of the three shapes in §3 and reachable in a browser; permission to stop and start
that service, or recreate that container, **once** at the end of your platform section — that restart loads the plugin; and, on the
two bare-metal shapes, the build tools that section lists (only Windows needs a .NET SDK, because only there is a wrapper compiled).

**Runtime evidence.** Every result in `docs/validation.md` was observed on Linux — the official container and the Debian/Ubuntu
package. The Windows route in §6 is written from published installer and service behaviour and has never been executed by this
project, so its verify step is evidence *you* collect. The container and package shapes have recorded Linux validation; the
release-asset wrapper download itself is not separately runtime-validated.

### 1.1 The three runtime files

All three live in **one** directory and carry **exactly** these names (on Windows, with `.exe` on the end):

| File | What it is | Where you get it |
| --- | --- | --- |
| `anaglyfin-ffmpeg` | the wrapper — the file Jellyfin's FFmpeg path points at | Docker and Linux: the linux-x64 release asset. Windows: published from the same tag |
| `ffmpeg-mvc` | the FFmpeg-mvc build the wrapper hands Anaglyfin commands to | built from tag `n8.1.3-mvc11-jf2` on every platform |
| `ffprobe` | the probe the server runs beside the wrapper | the `ffprobe` of the **official** Jellyfin FFmpeg already on the server — **never** the one the FFmpeg-mvc build produces |

| Shape | Runtime files | `lock` and `wrapper` directories |
| --- | --- | --- |
| Docker | `/config/anaglyfin/ffmpeg` | `/config/anaglyfin/lock`, `/config/anaglyfin/wrapper` |
| Debian/Ubuntu package | `/opt/anaglyfin/ffmpeg` | `/var/lib/jellyfin/anaglyfin/lock`, `/var/lib/jellyfin/anaglyfin/wrapper` |
| Windows service | `C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg` | beside it: `…\anaglyfin\lock`, `…\anaglyfin\wrapper` |

None of those names is a preference:

1. **The sidecar `ffprobe` is the official one.** The server probes media through it during ordinary playback, so it must match the
   stock FFmpeg. The FFmpeg-mvc build does write its own `ffprobe`; installing that beside the wrapper is wrong on every platform.
2. **The sidecar name and directory are derived, not configured.** Jellyfin replaces the **whole final filename** of the FFmpeg path
   it was given with `ffprobe`, keeps the extension, and looks in that directory alone — never on `PATH`. So
   `…\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe` is probed with `…\anaglyfin\ffmpeg\ffprobe.exe`, never `anaglyfin-ffprobe.exe`.
3. **No quotes around a path.** The server hands the path to the operating system rather than to a shell, so a space is fine and a
   literal `"` is part of the name — a quoted path is a file that cannot be found. The wrapper trims its own variables, never
   de-quotes them.

## 2. Add the plugin — do not restart yet

In **Dashboard → Plugins → Catalogs → Repositories → Add plugin repository**, enter:

| Field | Value |
| --- | --- |
| Name | `Anaglyfin` |
| Url | `https://raw.githubusercontent.com/tthayer93/Anaglyfin/metadata/manifest.json` |

Install **Anaglyfin** from the catalog, then **leave the server alone**: a plugin is loaded when the server starts, and the
last step of your platform section performs exactly one restart — that one loads it. Restarting now only buys a second one.

**If that repository cannot be added** — no route to `raw.githubusercontent.com`, say — unpack `Anaglyfin_<version>.zip` from the
[GitHub release](https://github.com/tthayer93/Anaglyfin/releases) into the server's own plugin directory: `/config/plugins/Anaglyfin`
for a container mounting `./jellyfin/config` at `/config`; `/var/lib/jellyfin/plugins/Anaglyfin` on the package, then
`sudo chown -R jellyfin:jellyfin` on it; `C:\ProgramData\Jellyfin\Server\plugins\Anaglyfin` on the Windows service, which is the
service's own data directory. The catalog installs this same archive, so the plugin is identical either way — and it never bundles
or installs the runtime files; the platform section you now follow does.

**The catalog moves the plugin and nothing else, on an update as well as on an install.** The catalog entry replaces the plugin
DLL; `anaglyfin-ffmpeg` is a separate asset of the same release, so a catalog update leaves the wrapper exactly where the last
install put it. Plugin and wrapper ship under one tag and have to be at that tag together, so the catalog is only half of an
update: the platform section's update step — §4.2, §5.2 or §6.2 — is what carries the wrapper to the new release, and taking
the plugin without it is the failure §8's wrapper entry covers.

## 3. Choose one platform and follow that section

| Your server, already installed and running | The service | The five variables go in | Follow |
| --- | --- | --- | --- |
| Official container `jellyfin/jellyfin:12.4` | the container, recreated by compose | compose `environment:` | §4 Docker |
| Debian/Ubuntu package | `jellyfin`, a systemd unit | a systemd drop-in of `Environment=` lines | §5 Linux package |
| Official Windows installer | `JellyfinServer`, hosted by the bundled NSSM | the service's `AppEnvironmentExtra` value | §6 Windows |

Pick **one** and follow it end to end; none of them installs Jellyfin. The five variables are the same five everywhere —
`JELLYFIN_FFMPEG`, `ANAGLYFIN_REAL_FFMPEG`, `ANAGLYFIN_SERVER_FFMPEG`, `ANAGLYFIN_LOCK_DIR`, `ANAGLYFIN_WRAPPER_SETTINGS` — and only
who hands them to the service differs. **Every platform section uses one lifecycle order;** do not read an update step into an initial
install, or the reverse.

- **Initial install:** prerequisites → directories → wrapper → FFmpeg-mvc → official `ffprobe` → ownership → the five variables
  (plus Docker's optional GPU mapping when used) → **one** restart or recreate → verify.
- **Update** — plugin and wrapper ship under one tag, so move them as a pair: (1) take the plugin update from the catalog,
  which waits for the restart at step 3 and replaces **only the plugin DLL** — the catalog never touches `anaglyfin-ffmpeg`,
  so the wrapper reaches the new tag in step 2 and not here; (2) replace the runtime file you are updating — wrapper,
  `ffmpeg-mvc`, or both — with the mechanics each shape makes safe: Docker execs every file operation inside the running
  container and swaps each file through a staged same-directory rename, stopping nothing; the bare-metal shapes stop the
  service, replace, and leave it stopped; (3) start, restart, recreate, or `daemon-reload` + start, **once**, which loads the
  plugin update too; (4) verify. A plugin taken without its wrapper is what §7's runtime-status wrapper row reports as a
  binary that did not answer `--anaglyfin-version`, and §8 says what to do about it.
- **Uninstall:** (1) take the plugin out of **Dashboard → Plugins** first — either order survives, this one is clearer;
  (2) remove **every** Anaglyfin environment entry, from every place you added one — the compose file on the host, the
  systemd drop-ins, or the service's `AppEnvironmentExtra`; (3) delete the Anaglyfin runtime directory and the `lock` /
  `wrapper` state: Docker execs the delete into the still-running container, bare metal deletes with the service stopped;
  (4) start, restart, or recreate, which puts the server back on its own FFmpeg; (5) clear or correct any path saved in
  **Dashboard → Playback → FFmpeg path**, which is used in preference to the FFmpeg in the server's own program directory.
  Uninstalling Anaglyfin **does not uninstall Jellyfin** and modifies no stock Jellyfin FFmpeg file.

## 4. Docker server

Targets **Linux x86-64** hosts, a service named `jellyfin`, the image `jellyfin/jellyfin:12.4`, and `./jellyfin/config` mounted at
`/config`. Every command execs as root with `-u root` rather than trusting whichever user the image runs its own process as. Two places
are involved: the runtime files and the `lock` / `wrapper` directories go under `/config`, the persisted volume, so they outlive the
container, while the apt packages of step 1 are **fetch-time and build-time only** and live in the container's own filesystem, so a recreate
discards them and nothing at run time needs them — the libraries the binaries load come from the image itself, mostly the
`/usr/lib/jellyfin-ffmpeg/lib` set its own FFmpeg uses, which the two `-rpath` lines below point a compiled binary at. Steps 3–5 are each one
self-contained block, and §4.2 updates by repeating them rather than by a second set of commands.

### 4.1 Initial install

**1. Build dependencies**, inside the running container: the compiler, `make`, `pkg-config`, and the headers the FFmpeg compile of step 4
reads, plus the `curl` and CA certificates the wrapper download of step 3 runs on. Repeat this block in any container **recreated or rebuilt**
since it last ran, **before** any later download or build — a plain `docker compose restart` keeps the packages, a recreate, a rebuild, or an
image pull discards them, because they live in the container's own filesystem rather than under `/config`. Nothing at run time needs them.
The ICU development package this page used to install is gone — it served an in-container wrapper publish, and the wrapper is downloaded now.
```sh
docker compose exec -u root jellyfin sh -c '
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    build-essential ca-certificates curl make nasm pkg-config \
    libass-dev libdrm-dev libfontconfig-dev libfreetype-dev libfribidi-dev \
    libnuma-dev libva-dev libvpl-dev libx264-dev zlib1g-dev
'
```

**2. Directories** — one for the three files, two for what the wrapper writes into while it runs.
```sh
docker compose exec -u root jellyfin install -d \
  /config/anaglyfin/ffmpeg /config/anaglyfin/lock /config/anaglyfin/wrapper
```
**3. The wrapper**, from the released linux-x64 asset, verified before it is installed. The release to fetch is the one assignment at the top
of the block, and the file lands through a staged same-directory rename, so this is also the block §4.2 repeats for a new wrapper — with that
one line changed and nothing else.
```sh
docker compose exec -u root jellyfin sh -c '
  set -e
  anaglyfin_tag=v0.2.4
  runtime=/config/anaglyfin/ffmpeg
  staged="$runtime/.anaglyfin-ffmpeg.new"
  rm -rf /tmp/anaglyfin-install
  mkdir -p /tmp/anaglyfin-install
  cd /tmp/anaglyfin-install
  curl -fsSL \
    "https://github.com/tthayer93/Anaglyfin/releases/download/$anaglyfin_tag/anaglyfin-ffmpeg" \
    -o anaglyfin-ffmpeg
  curl -fsSL \
    "https://github.com/tthayer93/Anaglyfin/releases/download/$anaglyfin_tag/SHA256SUMS.txt" \
    -o SHA256SUMS.txt
  grep " anaglyfin-ffmpeg$" SHA256SUMS.txt > anaglyfin-ffmpeg.sha256
  sha256sum -c anaglyfin-ffmpeg.sha256
  install -m 0755 anaglyfin-ffmpeg "$staged"
  mv -f "$staged" "$runtime/anaglyfin-ffmpeg"
'
```
`set -e` keeps the gate fail-closed: a failed download, a missing wrapper line, or a mismatched hash aborts the block before anything reaches
`/config`. Only the wrapper's line of `SHA256SUMS.txt` is checked, because that file names both release assets and this step fetches one of
them. Writing the dot-named file beside the target and `mv -f`'ing it there is the rename that makes the swap safe against a running binary
(§4.2), and it is what an already-started process keeps until it exits.

**4. FFmpeg-mvc: fetch the tag and compile it** in `/tmp`, build space nothing needs later. The tag to build is the assignment at the top of
the block, which is also the only line §4.2 changes when it repeats this step for a new FFmpeg-mvc. The last two `configure` lines are what
makes the result outlive the container that built it: they bake `/usr/lib/jellyfin-ffmpeg/lib` — where the official image keeps its FFmpeg
runtime libraries and the VA-API drivers — into the binary as its RUNPATH, so it loads them from there instead of from anything apt put in the
container. The compile is the slow part, and nothing is installed yet: that is step 5.
```sh
docker compose exec -u root jellyfin sh -c '
  set -e
  ffmpeg_mvc_tag=n8.1.3-mvc11-jf2
  curl -fsSL \
    "https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/$ffmpeg_mvc_tag.tar.gz" \
    -o /tmp/ffmpeg-mvc.tar.gz
  rm -rf /tmp/ffmpeg-mvc-src
  mkdir -p /tmp/ffmpeg-mvc-src
  tar -xzf /tmp/ffmpeg-mvc.tar.gz -C /tmp/ffmpeg-mvc-src --strip-components=1
  cd /tmp/ffmpeg-mvc-src
  ./configure \
    --disable-doc --enable-gpl --enable-libx264 --enable-libass --enable-vaapi --enable-libvpl \
    --extra-ldflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib \
    --extra-ldexeflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib
  make -j"$(nproc)"
'
```
**5. Install the two FFmpeg files.** Only the `ffmpeg` half of that build is installed, as `ffmpeg-mvc`; the build's own `ffprobe` is
deliberately left unused and the `ffprobe` this directory needs is the image's **official** one. Both go in the way step 3's wrapper does —
mode `0755` at a dot-name beside the target, then `mv -f` over it — so this block installs files and nothing else, and repeating it during an
update touches neither ownership nor the running processes.
```sh
docker compose exec -u root jellyfin sh -c '
  set -e
  runtime=/config/anaglyfin/ffmpeg
  staged="$runtime/.ffmpeg-mvc.new"
  install -m 0755 /tmp/ffmpeg-mvc-src/ffmpeg "$staged"
  mv -f "$staged" "$runtime/ffmpeg-mvc"
  staged_probe="$runtime/.ffprobe.new"
  install -m 0755 /usr/lib/jellyfin-ffmpeg/ffprobe "$staged_probe"
  mv -f "$staged_probe" "$runtime/ffprobe"
'
```
**6. Hand the tree to the server.** This is the ownership of the whole `/config/anaglyfin` tree, kept as its own step precisely so the file
installs above can be repeated without repeating it. It matters when `./jellyfin/config` belongs to a host user, because the server writes its
concurrency slots and settings document into that tree; on the official image, where that owner is root, it is a no-op.
```sh
docker compose exec -u root jellyfin sh -c \
  'set -e; chown -R "$(stat -c "%u:%g" /config)" /config/anaglyfin'
```
**7. Environment** — add to the service, in its compose file on the host — the one edit of this install made outside the
running container, because the container is started *from* that file and cannot carry or rewrite it:
```yaml
environment:
  JELLYFIN_FFMPEG: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg
  ANAGLYFIN_REAL_FFMPEG: /config/anaglyfin/ffmpeg/ffmpeg-mvc
  ANAGLYFIN_SERVER_FFMPEG: /usr/lib/jellyfin-ffmpeg/ffmpeg
  ANAGLYFIN_LOCK_DIR: /config/anaglyfin/lock
  ANAGLYFIN_WRAPPER_SETTINGS: /config/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```
The two FFmpeg variables split the work: `ANAGLYFIN_REAL_FFMPEG` receives every Anaglyfin marker/profile command, and
`ANAGLYFIN_SERVER_FFMPEG` — the stock FFmpeg the image ships, the one the server would have run — receives every ordinary command and
Jellyfin's startup capability probes, so a codec or tone-mapping tool the server read off its own FFmpeg is genuinely there for the commands
built around it. Unset or blank keeps the older single-binary behaviour, where every command goes to `ANAGLYFIN_REAL_FFMPEG`. Nothing is
quoted: these strings reach the process as written.

**8. Optional — Intel GPU.** The image ships the `iHD`/`i965` VA-API drivers and the `libva`/`libvpl` runtime, but Docker
hands the container the host GPU only if you map it:
```yaml
devices:
  - /dev/dri
```
Without the mapping the install still works and so does software encoding; `h264_vaapi` and `h264_qsv` hardware encoding does
not.

**9. Recreate the service once**, so it starts with that environment — and so the plugin from §2 loads:
```sh
docker compose up -d --force-recreate jellyfin
```
Nothing from steps 1–6 needs repeating afterwards: the recreate throws away packages that only ever fed the wrapper download and the compiler,
while what the server runs is the three files under `/config` and the ownership they carry, left where they were, on the FFmpeg libraries that
come back with the image. A recreate is `up -d --force-recreate`, an image pull, or `down` and `up`; a plain `docker compose restart` is not
one and needs nothing. The one thing that can move underneath this is a new image carrying different FFmpeg libraries: if
`docker compose exec -u root jellyfin ldd /config/anaglyfin/ffmpeg/ffmpeg-mvc` reports `not found`, redo steps 4–5.

### 4.2 Update

An update **repeats the numbered steps of §4.1** rather than following a second copy of them, and it stops nothing: an update is never a
`stop`, never a `cp`, and never a fetch, checksum, `chmod`, or file write on the Docker host, which supplies compose control only. Editing the
compose file belongs to the moments its configuration itself changes, §4.1 and §4.3, and is never an update step.

Nothing has to be stopped, because steps 3 and 5 already install that way: each file is written at mode `0755` to a dot-name beside its target
in the same runtime directory and then `mv -f`'d over it. A running binary cannot be written over — the write fails `ETXTBSY` — but a rename
disturbs no running process, and being same-directory it is atomic. A transcode running through the swap keeps its old binary until it exits,
and every process started after the rename already runs the new file.

1. **Plugin.** Take the update from the catalog first (§2) and start no restart yet: plugin and wrapper ship under one tag, so the single
   restart at step 5 of this section loads both. The catalog writes only the plugin DLL, so this step moves no runtime file — step 3 is what
   carries `anaglyfin-ffmpeg` to the new tag, and stopping here is a new plugin over an old wrapper (§8).

2. **Temporary tools.** **Repeat step 1** if the container has been recreated or rebuilt since that block last ran — `docker compose restart`
   alone does not discard those packages, while a recreate, a rebuild, or an image pull does. They are fetch-time and build-time only, never
   runtime dependencies, but they are what step 3's `curl` and CA trust and step 4's compiler and headers are made of, so a download or a
   compile in such a container fails without them. They are missing only in that case, so an update to a container that was merely restarted
   skips this item.

3. **New Anaglyfin wrapper and plugin.** **Repeat step 3**, changing only its `anaglyfin_tag=` assignment to the new release. That block
   re-fetches the tag's pair of assets, checks the wrapper's checksum line fail-closed, and renames the new binary over the running one; no
   other line of it changes. Repeat it whenever the catalog update at item 1 was taken: `anaglyfin_tag=` and the tag the catalog installed are
   one release, and the runtime-status wrapper row (§7) is the check that they match.

4. **New FFmpeg-mvc.** **Repeat step 4**, changing only its `ffmpeg_mvc_tag=` assignment, then **repeat step 5** unchanged. The source arrives
   as a tarball, so moving to a new tag means a new tarball rather than a `git` command in `/tmp`. Keep both `-rpath` lines: they bake
   `/usr/lib/jellyfin-ffmpeg/lib`, the image's own FFmpeg libraries and VA-API drivers, into the binary, which is what lets the result keep
   running after a recreate discards the build packages. Step 5 also refreshes the sidecar from the image's **official** `ffprobe`; that
   build's own `ffprobe` is still never installed.

5. **Restart once**, after all the updates you selected:

   ```sh
   docker compose restart jellyfin
   ```

   Every newly started process already runs the atomically renamed files, so this restart primarily loads the plugin catalog update and
   refreshes the Jellyfin and helper processes already running. A plain `restart` is enough, because no environment entry and no image
   changed.

Two things stay out of a routine update. **Ownership (step 6)** is not part of it: steps 3 and 5 write executable files at mode `0755` and
leave the `lock` and `wrapper` directories alone, so re-run step 6 only for a reason you can point at — the UID/GID arrangement changed,
something outside this flow recreated those directories root-owned, or the log reports permission errors writing the slots or the settings
document. And a **new Jellyfin image** can move the FFmpeg libraries underneath `ffmpeg-mvc`: if
`docker compose exec -u root jellyfin ldd /config/anaglyfin/ffmpeg/ffmpeg-mvc` reports `not found`, redo steps 4–5.

### 4.3 Uninstall

1. Take **Anaglyfin** out of **Dashboard → Plugins**, leaving the container running: the delete below execs into it,
   exactly like every install and update command does.
2. Take **every** Anaglyfin entry out of the compose file — all five of step 7 plus any optional one added beside them,
   such as `ANAGLYFIN_SLOT_WAIT_MS` or the `ANAGLYFIN_OFFICIAL_FFMPEG` alias. That edit is inherently host work: the
   environment lives in the compose file rather than in the container, and a container cannot rewrite the config it starts
   from — this is config control, not a file operation on the host, which deletes nothing. It is also safe to make with
   the container still running: the edit reaches only containers created after it, so the one already up keeps its
   environment and can still be exec'd into for the delete. Completeness is what matters: a recreated server whose
   compose file still names the deleted wrapper in `JELLYFIN_FFMPEG` looks for its FFmpeg at a path that is gone.
3. Delete the runtime files and the state beside them from inside the running container:

   ```sh
   docker compose exec -u root jellyfin rm -rf /config/anaglyfin
   ```

   From this moment until the recreate at item 4 below, the live server's own environment still names the deleted
   wrapper and every playback attempt fails: recreate immediately, and start no playback in that window.

4. Recreate with `jellyfin/jellyfin:12.4` — the stock container again, its own FFmpeg, its build packages discarded with
   the container that held them:

   ```sh
   docker compose up -d --force-recreate jellyfin
   ```

5. Clear any path saved in **Dashboard → Playback → FFmpeg path**.

This does not uninstall Jellyfin and leaves the image's own `/usr/lib/jellyfin-ffmpeg` files alone.

## 5. Linux package

Assumes the Debian/Ubuntu `jellyfin` package is installed and its service running: this section hands Anaglyfin's runtime
files to that service, it does not install the package. Run as a user with `sudo`.

### 5.1 Initial install

**1. Confirm the service and its stock FFmpeg pair** — that pair supplies both the `ANAGLYFIN_SERVER_FFMPEG` binary and the
sidecar `ffprobe`:
```sh
systemctl status jellyfin --no-pager
ls -l /usr/lib/jellyfin-ffmpeg/ffmpeg /usr/lib/jellyfin-ffmpeg/ffprobe
```
**2. Directories**, owned by the service account from the start:
```sh
sudo install -d -m 0755 /opt/anaglyfin/ffmpeg
sudo install -d -m 0755 -o jellyfin -g jellyfin \
  /var/lib/jellyfin/anaglyfin/lock /var/lib/jellyfin/anaglyfin/wrapper
```
**3. The wrapper**, from the released linux-x64 asset, verified before it is installed. `SHA256SUMS.txt` names both release
assets, so check the wrapper line rather than the file unless you also downloaded the plugin archive:
```sh
set -e
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/v0.2.4/anaglyfin-ffmpeg -o /tmp/anaglyfin-ffmpeg
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/v0.2.4/SHA256SUMS.txt -o /tmp/SHA256SUMS.txt
cd /tmp
grep " anaglyfin-ffmpeg$" SHA256SUMS.txt > anaglyfin-ffmpeg.sha256
sha256sum -c anaglyfin-ffmpeg.sha256
sudo install -m 0755 -o jellyfin -g jellyfin /tmp/anaglyfin-ffmpeg /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
```

**4. Build dependencies** for FFmpeg-mvc. No .NET SDK and no ICU development package appears here: the wrapper above was
downloaded, not compiled.
```sh
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
  build-essential ca-certificates curl make nasm pkg-config \
  libass-dev libdrm-dev libfontconfig-dev libfreetype-dev libfribidi-dev \
  libnuma-dev libva-dev libvpl-dev libx264-dev zlib1g-dev
```

**5. Build FFmpeg-mvc** in `/tmp`, from the tag the Docker section compiles. The two `-rpath` lines point the result at the FFmpeg runtime
libraries the package installs beside the stock FFmpeg, which is what lets it run on a host carrying no build packages.
```sh
curl -fsSL https://github.com/tthayer93/FFmpeg-mvc/archive/refs/tags/n8.1.3-mvc11-jf2.tar.gz -o /tmp/ffmpeg-mvc.tar.gz
rm -rf /tmp/ffmpeg-mvc-src
mkdir -p /tmp/ffmpeg-mvc-src
tar -xzf /tmp/ffmpeg-mvc.tar.gz -C /tmp/ffmpeg-mvc-src --strip-components=1
cd /tmp/ffmpeg-mvc-src
./configure \
  --disable-doc --enable-gpl --enable-libx264 --enable-libass --enable-vaapi --enable-libvpl \
  --extra-ldflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib \
  --extra-ldexeflags=-Wl,-rpath,/usr/lib/jellyfin-ffmpeg/lib
make -j"$(nproc)"
```

**6. Install `ffmpeg-mvc`, and the official `ffprobe` beside it** — the stock one, never the `ffprobe` that build also wrote:
```sh
sudo install -m 0755 -o jellyfin -g jellyfin /tmp/ffmpeg-mvc-src/ffmpeg /opt/anaglyfin/ffmpeg/ffmpeg-mvc
sudo install -m 0755 -o jellyfin -g jellyfin /usr/lib/jellyfin-ffmpeg/ffprobe /opt/anaglyfin/ffmpeg/ffprobe
```

**7. The five variables**, in a systemd drop-in; create the drop-in directory first with
`sudo install -d /etc/systemd/system/jellyfin.service.d`. The **service** reads these — your shell's environment reaches a running service
nowhere — and nothing is quoted.
```ini
# /etc/systemd/system/jellyfin.service.d/anaglyfin.conf
[Service]
Environment=JELLYFIN_FFMPEG=/opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
Environment=ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc
# The stock FFmpeg this server would otherwise run. If the server was pointed at a
# different FFmpeg before Anaglyfin, name that one instead of guessing.
Environment=ANAGLYFIN_SERVER_FFMPEG=/usr/lib/jellyfin-ffmpeg/ffmpeg
Environment=ANAGLYFIN_LOCK_DIR=/var/lib/jellyfin/anaglyfin/lock
Environment=ANAGLYFIN_WRAPPER_SETTINGS=/var/lib/jellyfin/anaglyfin/wrapper/anaglyfin-wrapper-settings.json
```

**8. Apply it and restart once** — which also loads the plugin from §2:
```sh
sudo systemctl daemon-reload
sudo systemctl restart jellyfin
```

### 5.2 Update

Take the plugin update from the catalog first (§2); plugin and wrapper are one release and move together, and the catalog
carries only the plugin DLL — the block below is what takes `anaglyfin-ffmpeg` to the tag the plugin came from, and a server
that takes only the catalog update keeps the old wrapper (§8). Stop the service before replacing either file, and start it
once at the end.
```sh
set -e
sudo systemctl stop jellyfin
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/<new-tag>/anaglyfin-ffmpeg -o /tmp/anaglyfin-ffmpeg
curl -fsSL https://github.com/tthayer93/Anaglyfin/releases/download/<new-tag>/SHA256SUMS.txt -o /tmp/SHA256SUMS.txt
cd /tmp
grep " anaglyfin-ffmpeg$" SHA256SUMS.txt > anaglyfin-ffmpeg.sha256
sha256sum -c anaglyfin-ffmpeg.sha256
sudo install -m 0755 -o jellyfin -g jellyfin /tmp/anaglyfin-ffmpeg /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg
sudo systemctl start jellyfin
```
For FFmpeg-mvc, download the new tag's tarball and rebuild it exactly as in step 5, both `-rpath` lines included, then swap the compiled
`ffmpeg` over `/opt/anaglyfin/ffmpeg/ffmpeg-mvc` with the service stopped and start again. Never install that build's `ffprobe`: the sidecar
stays the stock one. The drop-in needs no change unless a path inside it moved; if it did, `daemon-reload` before the start.

### 5.3 Uninstall

1. Take **Anaglyfin** out of **Dashboard → Plugins**, then `sudo systemctl stop jellyfin`.
2. Delete `/etc/systemd/system/jellyfin.service.d/anaglyfin.conf` **and every other Anaglyfin environment entry, wherever you
   put it** — an optional `ANAGLYFIN_SLOT_WAIT_MS` or the `ANAGLYFIN_OFFICIAL_FFMPEG` alias may sit in another drop-in.
3. Remove the runtime files and the state beside them, then reload and start, which puts the server back on the stock FFmpeg:

   ```sh
   sudo rm -rf /opt/anaglyfin /var/lib/jellyfin/anaglyfin
   sudo systemctl daemon-reload
   sudo systemctl start jellyfin
   ```

4. Clear any path saved in **Dashboard → Playback → FFmpeg path**.

Jellyfin and its `/usr/lib/jellyfin-ffmpeg` files are untouched.

## 6. Windows official service

What §5 does on Linux, on Windows: the same three runtime files under `C:\ProgramData\Jellyfin\Server\anaglyfin`, the same
five variables handed to the `JellyfinServer` service instead of to systemd. Everything below is PowerShell (5.1 or 7)
**elevated to Administrator** except step 4, which runs in **MSYS2 CLANG64**.

> **Status: documented, not runtime-validated.** No Windows host has run this wrapper or an FFmpeg-mvc build in this
> project's CI or validation runs, so nothing below is evidence — your own verify pass is. Two facts force local builds:
> there is no `win-x64` wrapper asset, and FFmpeg-mvc publishes no binaries for any platform.

### 6.1 Initial install

**1. Prerequisites** — checked before anything is built.
```powershell
Get-Service JellyfinServer
Get-ChildItem "C:\Program Files\Jellyfin\Server\ffmpeg.exe", "C:\Program Files\Jellyfin\Server\ffprobe.exe"
git --version
dotnet --version
```
- **`JellyfinServer` exists and starts.** Only the official installer creates it; if it is missing, Jellyfin's own documentation is the next
  step, not this page — a portable zip or a hand-run `jellyfin.exe` is not the shape anything here configures.
- **Both stock FFmpeg files are present.** `ffmpeg.exe` becomes `ANAGLYFIN_SERVER_FFMPEG`, `ffprobe.exe` the sidecar. Get a missing one from
  Jellyfin, and never point `ANAGLYFIN_SERVER_FFMPEG` at the FFmpeg-mvc build.
- **Git, the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (the SDK, not the runtime) and MSYS2 with its
  [CLANG64](https://www.msys2.org/) environment** serve steps 3 and 4, all three build-time only: the wrapper publishes self-contained, so
  the server needs no .NET runtime to run it.

**2. Directories, and the right to write them.**
```powershell
$anaglyfinRoot = "C:\ProgramData\Jellyfin\Server\anaglyfin"
New-Item -ItemType Directory -Force -Path `
  "$anaglyfinRoot\ffmpeg", "$anaglyfinRoot\lock", "$anaglyfinRoot\wrapper" | Out-Null
icacls $anaglyfinRoot /grant "NT AUTHORITY\NETWORK SERVICE:(OI)(CI)M" /T
```
`ffmpeg` holds the three runtime files; `lock` and `wrapper` are written **while the service runs** — the wrapper's concurrency slots, the
plugin's settings document — which is what the grant is for. The installer runs the service as **Network Service**; if this service got
another account, grant to that one, reading it with
`Get-CimInstance Win32_Service -Filter "Name='JellyfinServer'" | Select-Object -ExpandProperty StartName`.

**3. Build the wrapper for `win-x64`** — there is no Windows asset to download, so the tag is published locally. Clone the tag the catalog
installed rather than a branch tip: `v0.2.4` is what §2 installs, and a release bumps plugin and wrapper together.
```powershell
git clone --depth 1 --branch v0.2.4 https://github.com/tthayer93/Anaglyfin.git C:\build\anaglyfin-wrapper-src
cd C:\build\anaglyfin-wrapper-src
dotnet publish .\src\Anaglyfin.FFmpegWrapper\Anaglyfin.FFmpegWrapper.csproj `
  --configuration Release --runtime win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded `
  --output C:\build\anaglyfin-wrapper
Rename-Item C:\build\anaglyfin-wrapper\Anaglyfin.FFmpegWrapper.exe anaglyfin-ffmpeg.exe
```
Rename **only** that `.exe`: the apphost locates its managed entry point from build time rather than from its filename, so Jellyfin may see
any name. If the output is a `.dll` plus a `.runtimeconfig.json` instead of one `.exe`, single-file did not apply — those two are loaded by
name and must not be renamed.

**4. Build FFmpeg-mvc** *(MSYS2 CLANG64; advanced; never run by this project).* FFmpeg-mvc carries no Windows build scripts, so the parent
project's are copied in beside the source and run there. Open **MSYS2 CLANG64** — not plain `MSYS` and not `MINGW64`: the toolchain installs
into CLANG64 and the other two shells cannot see it. The two tags belong together, and the parent checkout supplies only its `msys2/` scripts.
```bash
pacman -Syu --noconfirm
pacman -S --needed --noconfirm \
  git curl wget zip unzip \
  quilt diffstat \
  mingw-w64-clang-x86_64-toolchain \
  mingw-w64-clang-x86_64-nasm
mkdir -p /c/build/anaglyfin
cd /c/build/anaglyfin
git clone --depth 1 --branch v8.1.3-2 https://github.com/jellyfin/jellyfin-ffmpeg.git
git clone --depth 1 --branch n8.1.3-mvc11-jf2 https://github.com/tthayer93/FFmpeg-mvc.git
cp -r jellyfin-ffmpeg/msys2 FFmpeg-mvc/
cd FFmpeg-mvc
bash msys2/build.sh
```
**The artifact is `/c/build/anaglyfin/FFmpeg-mvc/ffmpeg.exe`** — step 5 spells it `C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe`. Everything
`build.sh` does after `make` is the parent's packaging: it reads `debian/changelog`, which an FFmpeg-mvc product tag does not have, so the
script can exit non-zero **after a build that succeeded** over a file already built in the source root; only a script that ran to its end has
moved that file under `msys2\`. If it fails, do not invent `configure` flags — start again from the parent's published script and its
dependencies, or take the question to the FFmpeg-mvc maintainer, because a hand-rolled flag set is how you get a binary advertising an
encoder it cannot run.

**5. Copy the three runtime files.**
```powershell
$ffmpegDir = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg"
Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe "$ffmpegDir\anaglyfin-ffmpeg.exe" -Force
Copy-Item C:\build\anaglyfin\FFmpeg-mvc\ffmpeg.exe        "$ffmpegDir\ffmpeg-mvc.exe"       -Force
# the STOCK ffprobe, from the installer — never the one step 4 produced
Copy-Item "C:\Program Files\Jellyfin\Server\ffprobe.exe"  "$ffmpegDir\ffprobe.exe"          -Force
```
They inherit the modify right step 2's grant put on those directories; `Get-ChildItem $ffmpegDir` now shows three `.exe` files.

**6. Service environment — the service's own, not your shell's.** A started helper inherits the environment **the service** was started with,
so your session variables and `setx` reach nothing. NSSM keeps that environment under the service's own key; this block reads what is there,
keeps everything neither empty nor an Anaglyfin name, adds the five, and writes the union back:
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
# On a server never configured this way the value does not exist yet, the read reports
# nothing, and $kept is empty.
$existing = @(
  (Get-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
     -ErrorAction SilentlyContinue).AppEnvironmentExtra
)
$kept = @($existing | Where-Object { $_ -and $_ -notmatch '^(JELLYFIN_FFMPEG=|ANAGLYFIN_)' })
Set-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
  -Value ($kept + $anaglyfinEnvironment) -Type MultiString
```
Three rules about that value, each load-bearing: **`AppEnvironmentExtra`, never `AppEnvironment`** — the first is *appended* to the
environment the service starts with, the second **replaces** it, leaving Jellyfin no `PATH` and no `SystemRoot`; **`REG_MULTI_SZ`, one
`NAME=VALUE` per entry**, exactly what `nssm.exe set JellyfinServer AppEnvironmentExtra NAME=VALUE …` writes, so either tool is fine and
neither is a second configuration to keep in sync, and several assignments never share an entry; and **no quotes around a path** —
`ANAGLYFIN_SERVER_FFMPEG` above points into `C:\Program Files\...` and is correct as written, while the same path in literal `"` characters
names a file that cannot be found.

`ANAGLYFIN_WRAPPER_SETTINGS` is not one to skip: the plugin writes its subtitle-depth and concurrency document to exactly that
path, and a wrapper can only read a document it is *told* about. Unset, the file sits under `plugins\configurations` where no
wrapper looks, and subtitle depth quietly stays flat with nothing logged as an error.

**7. Restart once:**
```powershell
Restart-Service JellyfinServer
```
NSSM reads `Parameters` when it launches the server, so the new environment belongs only to processes started after this command — and this is
also the restart that loads the plugin from §2. Then run §7: on this shape your own verify pass is the only evidence that exists.

### 6.2 Update

Take the plugin update from the catalog first (§2), then swap the file with the service stopped — a running `.exe` cannot be overwritten — and
start it again. The catalog brings the plugin DLL and nothing else, so the publish below is what moves `anaglyfin-ffmpeg.exe` to the tag the
plugin came from; a server that takes only the catalog update keeps the wrapper it already had (§8).

**The wrapper:** the tag changes, the clone does not, because `git clone` refuses an existing directory and the old tag leaves
a detached HEAD that `--detach` moves off cleanly.
```powershell
cd C:\build\anaglyfin-wrapper-src
git fetch --depth 1 origin "<new-tag>"
git checkout --detach FETCH_HEAD
```
Re-run step 3's publish block unchanged, rename its output as step 3 does, then swap it with the service stopped:
```powershell
Stop-Service JellyfinServer
Copy-Item C:\build\anaglyfin-wrapper\anaglyfin-ffmpeg.exe `
  "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -Force
Start-Service JellyfinServer
```
**FFmpeg-mvc:** the same two git commands in its clone at `C:\build\anaglyfin\FFmpeg-mvc`, rebuild, and swap `ffmpeg-mvc.exe` the same way
(use the build's source-root `ffmpeg.exe` unless the copied `msys2\` packaging moved it). The copied `msys2\` belongs to neither tag, so it
stays where it is. Do **not** replace `ffprobe.exe` from that build.

### 6.3 Uninstall

1. Take **Anaglyfin** out of **Dashboard → Plugins**, then `Stop-Service JellyfinServer`.
2. Take Anaglyfin's entries out of `AppEnvironmentExtra` by reading the current value, keeping entries that are not `JELLYFIN_FFMPEG=` or
   `ANAGLYFIN_`, writing the survivors back as `REG_MULTI_SZ` when any remain, and deleting the value only when it held entries but none
   survive; then remove the runtime directory — §6.1 step 2's `icacls` grant goes with the directory it was applied to — and restart the
   service:

   ```powershell
   $parameters = "HKLM:\SYSTEM\CurrentControlSet\Services\JellyfinServer\Parameters"
   $existing = @(
     (Get-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
        -ErrorAction SilentlyContinue).AppEnvironmentExtra
   )
   $kept = @($existing | Where-Object { $_ -and $_ -notmatch '^(JELLYFIN_FFMPEG=|ANAGLYFIN_)' })
   if ($kept.Count -gt 0) {
     Set-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
       -Value $kept -Type MultiString
   } elseif ($existing.Count -gt 0) {
     Remove-ItemProperty -Path $parameters -Name AppEnvironmentExtra `
       -ErrorAction SilentlyContinue
   }
   Remove-Item -Recurse -Force "C:\ProgramData\Jellyfin\Server\anaglyfin"
   Restart-Service JellyfinServer
   ```

3. Clear any path saved in **Dashboard → Playback → FFmpeg path**, which is used in preference to the server's own program directory and so is
   where that correction belongs.

`JELLYFIN_FFMPEG` left naming the deleted wrapper is what bites: the server looks for its FFmpeg where the wrapper used to be. This does not
uninstall Jellyfin — everything under `C:\Program Files\Jellyfin\Server`, its `ffmpeg.exe` and `ffprobe.exe` and its NSSM included, stays
exactly as Jellyfin installed it.

## 7. Verify

Run these once, on the shape you installed. `docs/validation.md` is the full checklist and the recorded results; every row recorded there is a
Linux result, and Windows has none.

**1. The plugin page.** `Dashboard → Plugins` lists **Anaglyfin** and its settings page opens.

The page's **Runtime status** section, just below the deployment blurb, says what the plugin itself can see of the
installation — read it before reading the server log. It confirms:

- the binaries an Anaglyfin command can reach — the Anaglyfin FFmpeg wrapper, the FFmpeg-mvc build, the official/server
  FFmpeg and the derived `ffprobe` sidecar — each with the state its checks turned out to be, the path it resolved, and
  the version it answered with;
- the five deployment variables it reports — `JELLYFIN_FFMPEG`, `ANAGLYFIN_REAL_FFMPEG`, `ANAGLYFIN_SERVER_FFMPEG`,
  `ANAGLYFIN_LOCK_DIR` and `ANAGLYFIN_WRAPPER_SETTINGS` — each with the value the deployment set, the path that resolved
  from it, and which variable supplied that value (`Required` says which of them the deployment has to set);
- whether the slot directory and the wrapper settings document are writable by the service account, which is the part
  checks 3 and 4 cannot prove because those run as **your** account;
- **Refresh**, which asks the plugin to probe again instead of answering from what it already has.

An FFmpeg-family row — the FFmpeg-mvc build, the official/server FFmpeg and the derived `ffprobe` — shows the whole
version token its binary printed after `ffmpeg version` / `ffprobe version` in its own `-version` banner, so the
FFmpeg-mvc row reads `n8.1.3-mvc11-jf2` rather than only the base `8.1.3` a plain numeric read would leave.

The section reads and never writes: it installs nothing, saves nothing and edits nothing. Placing a binary or setting a
variable stays a deployment step (§4-§6), and checks 3-5 stay the proof that what it reports also works. If the section
cannot reach the endpoint it says so in one line and leaves the settings usable.

The **Anaglyfin FFmpeg wrapper** row is the one to read after an update: its state comes from the wrapper's own
`--anaglyfin-version` answer, so a `Probe failed` there on a server whose plugin page opens is the plugin having moved without
its wrapper — §8 names the fix. `Version mismatch` is the same pairing on a wrapper new enough to name itself. The endpoint
behind the table, `GET /Anaglyfin/RuntimeStatus`, spells each row out in its own words: the stale wrapper says the binary did
not answer `--anaglyfin-version`, and a row saying the wrapper path **is the same file as** one of the two FFmpeg builds is
`JELLYFIN_FFMPEG` aimed at FFmpeg rather than at `anaglyfin-ffmpeg`.

**2. The server names the wrapper as its FFmpeg.** That startup line, naming the wrapper's path, is what identifies the
wrapper as Jellyfin's FFmpeg; no server-reported FFmpeg version is part of the contract.

| Shape | The line |
| --- | --- |
| Docker | `MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg` |
| Linux package | `MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg` |
| Windows service | `MediaBrowser.MediaEncoding.Encoder.MediaEncoder: FFmpeg: C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe` |

```sh
docker compose logs jellyfin 2>&1 | grep 'MediaEncoder: FFmpeg:'        # Docker
sudo journalctl -u jellyfin --no-pager | grep 'MediaEncoder: FFmpeg:'   # Linux package
```

```powershell
# Windows service; its log directory is C:\ProgramData\Jellyfin\Server\log
Select-String -Path "C:\ProgramData\Jellyfin\Server\log\*.log" -Pattern 'MediaEncoder: FFmpeg:' | Select-Object -Last 3
```
The Windows startup environment dump echoes only `JELLYFIN_`, `DOTNET_` and `ASPNETCORE_` names, so it proves `JELLYFIN_FFMPEG` and nothing
else — the checks below prove the binaries and the dispatch, and check 5's real transcode proves the service-written `lock` and `wrapper`
paths.

**3. The stock `ffprobe` is the sidecar.** Two matching first lines are the answer wanted, because an FFmpeg-mvc `ffprobe`
names the mvc tag in its banner.
```sh
# Docker
docker compose exec -u root jellyfin sh -c '
  /config/anaglyfin/ffmpeg/ffprobe -version | head -1
  /usr/lib/jellyfin-ffmpeg/ffprobe  -version | head -1'

# Linux package
/opt/anaglyfin/ffmpeg/ffprobe -version | head -1
/usr/lib/jellyfin-ffmpeg/ffprobe     -version | head -1
```

```powershell
# Windows service
& "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffprobe.exe" -version | Select-Object -First 1
& "C:\Program Files\Jellyfin\Server\ffprobe.exe"                -version | Select-Object -First 1
```

**4. The wrapper dispatches to FFmpeg-mvc.** `-version` is an ordinary command, so it goes to `ANAGLYFIN_SERVER_FFMPEG` when that variable is
set; blanking it for one invocation (a blank value counts as unset) sends the command down the marker route, so the banner that comes back is
the FFmpeg-mvc build's.
```sh
# Docker
docker compose exec -u root jellyfin \
  env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/config/anaglyfin/ffmpeg/ffmpeg-mvc \
  /config/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version

# Linux package
sudo -u jellyfin env ANAGLYFIN_SERVER_FFMPEG= ANAGLYFIN_REAL_FFMPEG=/opt/anaglyfin/ffmpeg/ffmpeg-mvc \
  /opt/anaglyfin/ffmpeg/anaglyfin-ffmpeg -version
```

```powershell
# Windows service
$env:ANAGLYFIN_REAL_FFMPEG = "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\ffmpeg-mvc.exe"
Remove-Item Env:ANAGLYFIN_SERVER_FFMPEG -ErrorAction SilentlyContinue
& "C:\ProgramData\Jellyfin\Server\anaglyfin\ffmpeg\anaglyfin-ffmpeg.exe" -version
```
These run as **your** account, so they prove dispatch and the binaries, not the service account's right to write the `lock` and `wrapper`
directories.

**5. One real Anaglyfin transcode plays**, from a client, with an MVC item and a profile enabled. Its `transcode_<n>.log` in the server's log
directory shows the wrapper's command line carrying the marker URL and the `ffmpeg-mvc` child's carrying the real source path and the
profile's filters with no marker in it (`docs/validation.md` V6/V7).

**6. The encoder Jellyfin selects exists in the FFmpeg-mvc build** — a one-directional check. Read `-codec:v` off that transcode log and
confirm it is listed by `ffmpeg-mvc -encoders`. Once the startup probes answer from the official binary, the server can reach for an encoder
the minimal build does not carry, so the official list being a superset is expected and is not itself a fault; a *marker* job naming an
encoder the build lacks is. The documented build flags give parity for the QSV/VA-API H.264 flow, not for every encoder a 12.x server can
select.

**7. Stopping a transcode stops cleanly** — the caveat specific to Windows. There are no POSIX signals there and the wrapper registers no
stop-signal handler, so a stop rests on the `q` the server writes to FFmpeg's stdin, which reaches the FFmpeg-mvc child through the stdin the
wrapper inherits. `Get-Process ffmpeg-mvc` after a stopped playback tells you whether a child was left behind, and §8 says what an orphan does
and does not cost you.

## 8. Troubleshooting

- **Server starts, ordinary playback works, and every Anaglyfin version fails with** `anaglyfin-wrapper: refused: the FFmpeg binary named by
  ANAGLYFIN_REAL_FFMPEG ('ffmpeg-mvc') is not an existing file.` In a two-binary deployment the startup probes and ordinary playback reach the
  stock FFmpeg, so a missing `ffmpeg-mvc` stays invisible until an Anaglyfin job asks for it. Install the file, or fix the variable naming it.
- **The server will not start at all:** `FFmpeg validation: The process returned no result` and `Path set by command line or environment
  variable is invalid`. That message blames the `JELLYFIN_FFMPEG` path — the wrapper — but in a single-binary deployment the startup probe
  itself goes to `ANAGLYFIN_REAL_FFMPEG`, so the missing file is usually the other one. Check both binaries before changing either path.
- **The runtime-status wrapper row reads `Probe failed` right after a catalog plugin update**, usually with the plugin page itself opening
  and ordinary playback fine. The catalog replaces only the plugin DLL (§2), so the `anaglyfin-ffmpeg` on disk is still the release before
  the plugin's — and that release predates the `--anaglyfin-version` question the status asks, so the wrapper has nothing to answer it with
  and the status answer for that row says the binary did not answer it (the plugin published as `v0.2.3` words that same failed probe
  `it is not a real wrapper` and calls it an error, so a page from that release offers less to act on than the words above).
  Take the wrapper to the plugin's own tag — §4.2's wrapper step, §5.2, §6.2 — and do the one restart; the row returns to `OK` with it. Two
  neighbours are different faults: `Version mismatch` names both releases because that wrapper is new enough to answer, and an answer saying
  the wrapper path **is the same file as** the FFmpeg-mvc build or the official/server FFmpeg is not a stale wrapper at all but
  `JELLYFIN_FFMPEG` aimed at FFmpeg, which §1.1 says how to correct.
- **`Running /config/anaglyfin/ffmpeg/ffprobe ... No such file or directory`**, and a library that never fills: no sidecar `ffprobe`. Copy the
  **official** one beside the wrapper — the derived name is always `ffprobe` in that same directory, never `anaglyfin-ffprobe`, and `PATH` is
  never consulted — then restart.
- **A path that exists and is still reported invalid** is quoted. Environment values reach the process as written and the server hands the path
  to the operating system rather than to a shell, so a literal `"` is part of the filename. Strip quotes from the environment entry, and from
  anything saved in **Dashboard → Playback → FFmpeg path**.
- **An encoder or filter the marker job needs is missing from `ffmpeg-mvc -encoders`.** The build lacks it: rebuild with build-flag parity
  against the official FFmpeg, or change what the server may select on its device profile. A wrapper change is not the fix — see check 6 above.
- **`Unrecognized option 'vbr:a'` and `Error splitting the argument list: Option not found`**, immediately after the child's banner with
  nothing after it, on an Anaglyfin version's child. That signature is the audio capability inversion: the official build's probe advertised
  `libfdk_aac`, so the server wrote `-codec:a… libfdk_aac` plus that encoder's private `-vbr:a <n>` into a marker command that runs on the
  minimal FFmpeg-mvc build, which carries neither and dies while parsing, before opening any input. The `v0.2.1` and later wrapper maps this
  one known selection onto native `aac` on the marker route — bitrate synthesized from a fixed table — and logs an `anaglyfin-wrapper:
  warning: the FFmpeg-mvc build …` line naming the fallback. On a `v0.2.0` wrapper the same job dies this way: turn off **Enable audio VBR** in
  the server's playback settings, or unset `ANAGLYFIN_SERVER_FFMPEG`. Any *other* capability gap stays the bullet above — the wrapper maps this
  one audio selection and nothing else.
- **Windows: `Get-Process ffmpeg-mvc` still shows a process after the playback stopped.** When a transcode ignores the `q`, the server waits
  five seconds and terminates the process it owns — the **wrapper** — and Windows kills no process tree with that call, so the FFmpeg-mvc child
  is orphaned. `Stop-Process` by that name, or another service restart, clears it. The concurrency slot does not go with the orphan: a slot
  file is opened `DeleteOnClose`, so the operating system releases it the moment the wrapper is gone and the next Anaglyfin playback is not
  locked out. A stop that *hangs* is worth writing up, because no run of this project has measured the `q` path on Windows.
- **Everything above passes and the picture is wrong**, or versions never appear for a file you expected: that is the product checklist rather
  than this page — `docs/validation.md` V4–V10.
