# RetroBat Folder Switcher

A small Windows tool for RetroBat that:

1. **Switches the roms folder** on launch: by which drives are plugged in, by whether your home server is on the local network, or a default.
2. **Builds library folders** made of links to system folders spread across several drives.
3. **Adds media links** to rom folders that were copied without their `media` folders.
4. **Runs robocopy syncs** one job at a time, with a preview and the full output in the log.

Everything is set up in one settings window. Launching RetroBat through the exe is silent.

## Install

1. Download `RetroBatFolderSwitcher.zip` from [Releases](../../releases) and put `RetroBatFolderSwitcher.exe` next to `retrobat.exe`.
2. Hold **Shift** while opening it (or run `RetroBatFolderSwitcher.exe --settings`) to open settings.
3. Click **Desktop shortcuts** if you want a "RetroBat (Folder Switcher)" launcher and a settings shortcut.
4. Launch RetroBat through `RetroBatFolderSwitcher.exe` from Steam, a desktop shortcut, Playnite and so on.

It needs .NET Framework 4.8, which ships with Windows 10 and 11.

**Tip:** turn on **Developer Mode** (Settings → System → For developers). It lets the tool create symlinks without "Run as administrator", which matters for network drives. Links between local drives work either way.

## Launch (folder switching)

![Launch](docs/settings.png)

| Rule | Condition | Roms folder used |
|---|---|---|
| 1. Docked | All the listed drives are connected | Docked folder |
| 2. Home network | Your home server is on the local network (wired or Wi-Fi, never through a VPN) | Home folder |
| 3. Anywhere else | Nothing above matched | Default folder (your original `roms`) |

Rules are checked from top to bottom, and the first one that matches wins. Each of the first two can be turned off.

RetroBat always reads `RetroBat\roms`. The tool turns `roms` into a **link** and points it at the chosen folder each launch, so `es_systems.cfg` (which RetroBat overwrites on updates) is never edited. A rule's folder can be a name inside the RetroBat folder or a full path, such as a library below.

- **First run:** if `roms` is a real folder, it's renamed to the default folder (`roms-default`). Nothing is ever deleted or merged.
- If a rule's folder doesn't exist, it falls back to the default. If RetroBat is already running, the link is left alone.
- **Right now** shows which rule would win at this moment, and **Test (dry run)** shows exactly what would happen.
- **Before launching** can update library links and/or media links first, so new systems show up automatically.
### How "Home network" works

A ping can't tell home from away when a VPN such as WireGuard is always on: the server answers either way. Instead, the tool sends an **ARP** request for the server from each wired or Wi-Fi adapter on the server's subnet. ARP only works on the physical network and never crosses a VPN tunnel. So if the server answers, it's on your local network, whether you're wired or on Wi-Fi.

- **Server address:** your server's IP or name (e.g. `192.168.1.6`).
- **Server MAC (optional, recommended):** many networks use `192.168.1.x`, so a hotel or a friend's network could have a different device at the same address. With the MAC set, only *your* server counts. Click **Use current** once while you're at home to fill it in.
- **Check** shows what was found in Activity.
- If Windows would still send traffic to the server through the VPN while you're at home, you get a warning. That happens when WireGuard's `AllowedIPs` includes your home subnet.

## Libraries

![Libraries](docs/libraries.png)

Each library is an output folder filled with one link per system folder (`nes`, `snes`, ...) found in its sources:

- **Sources are checked in order.** If two sources have the same system, the first one wins.
- Anything already in the output folder is left alone, and missing sources (an unplugged drive) are skipped with a warning.
- **Build** adds links for new systems in every included library. **Rebuild** removes and recreates the links, which you need after changing the source order. Only links are ever removed. **Preview** shows what Build would do.
- **Link type:** *Auto* makes a symlink if Windows allows it, otherwise a junction. *Symlink* needs Developer Mode or admin, and is required for network sources. *Junction* needs no admin but only works for local drives.

## Media links

![Media links](docs/media.png)

For rom folders copied without media (`robocopy ... /XD media`), this puts the media back with links:

```
E:\Roms\nes\media  ->  C:\Games\RetroBat\media\nes\media
```

- Every system folder in each **rom folder** gets a `media` link, when `<media folder>\<system>\media` exists.
- Systems without media are skipped, and an existing real `media` folder is never touched.
- `robocopy /MIR /XD media` never removes these links, so you can re-sync any time.
- **Create links** adds missing ones. **Recreate** removes and recreates them, never real folders.
- Libraries that link to these rom folders pick up the media automatically.

## Sync (robocopy)

![Sync](docs/sync.png)

Two lists of mirror jobs. Select one or more jobs and run only those, so a full sync never happens unless you ask for it.

| List | Command | You set |
|---|---|---|
| Rom sync | `robocopy IN OUT /MIR /XD media` | input folder, output folder |
| Media sync | `robocopy IN OUT /MIR /XD <excluded>` | input folder, output folder, excluded folders |

- Select jobs with Ctrl- or Shift-click (Ctrl+A selects all). Double-click a job to edit it.
- **Preview** runs robocopy with `/L`, which lists what would be copied and deleted without changing anything.
- **Run** shows the exact commands and asks for confirmation first.
- Robocopy's full output streams into the log window and `FolderSwitcher.log`. Each job ends with its exit code and what it means.
- **Stop** (next to the Activity status) cancels the running job, and any remaining jobs are skipped.
- **Only run when the home server is on the local network** (on by default): before running, the tool does the same check as the Home network rule. If the server can only be reached through WireGuard, the sync is skipped.
- **Extra robocopy options** (default `/R:2 /W:5 /NP`) are added to every job. Robocopy's default retry setting can wait for days on a single locked file.
- Excluded folders are separated by spaces or commas. Put quotes around names that contain spaces: `"my folder"`.
- **Safety checks:** a job won't run if the input is missing or empty (mirroring an empty folder would wipe the output), if the output is a whole drive or a system folder, or if the input and output overlap.

## Settings file

Everything is saved in `FolderSwitcher.json` next to the exe. Unknown or missing keys fall back to defaults, so older files keep working.

## Command line

| Option | Does |
|---|---|
| *(none)* | Update links if *Before launching* says so, switch the folder, launch RetroBat |
| `--settings` | Open the settings window (same as holding Shift) |
| `--dry-run` | Log what would happen, change nothing, don't launch |
| `--no-launch` | Switch the folder but don't start RetroBat |
| `--build-libraries` | Build libraries and media links, then exit (`--dry-run` to preview) |
| `--rebuild-libraries` | Remove and recreate library and media links, then exit |

Everything is logged to `FolderSwitcher.log` next to the exe. In settings, the **Activity** area shows the same output: right-click it for *Copy all*, *Clear* and *Open log file*. **Ctrl+S** saves.

## Build

```
dotnet build src/FolderSwitcher.csproj -c Release
```

Every push to `main` builds the exe on GitHub Actions. Bump `<Version>` in `src/FolderSwitcher.csproj` and push to publish a new release.
