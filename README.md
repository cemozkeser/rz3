<!--
  rz3/README.md  —  main project README

  Written for two readers at once:
    * a person who owns a Razer mouse and wants their buttons to work, and
    * an AI agent or developer who needs to understand or extend the protocol work.

  The protocol section is the important one: it is the reusable knowledge this project
  produced. Everything else can be regenerated; that cannot.
-->

# Rz3

**A complete replacement for Razer Synapse for the Razer Basilisk V3 Pro 35K, written from
scratch, that needs no driver, no administrator rights and no Razer software installed.**

Rz3 talks to the mouse directly over its own HID control channel. Profiles, button mappings,
DPI stages, polling rate, scroll behaviour, lighting and power settings are written into the
mouse's own memory, so they keep working with Razer Synapse uninstalled.

The interesting part of this project is not the GUI — it is the **protocol work**: how to
discover and reliably drive an undocumented vendor control channel on a locked-down HID device,
including the button-mapping command Razer never documented. That is documented in
[`docs/PROTOCOL.md`](docs/PROTOCOL.md) and summarised [below](#the-core-result-how-to-talk-to-the-mouse).

---

## Contents

- [What it does](#what-it-does)
- [Install](#install)
- [Use](#use)
- [The core result: how to talk to the mouse](#the-core-result-how-to-talk-to-the-mouse)
- [Architecture](#architecture)
- [Build from source](#build-from-source)
- [Testing](#testing)
- [Versioning and releases](#versioning-and-releases)
- [Known limits](#known-limits)
- [Safety](#safety)
- [License and trademarks](#license-and-trademarks)

---

## What it does

| Area | What Rz3 changes |
|---|---|
| **Profiles** | List, activate, create, clone, rename and delete the mouse's onboard profiles (up to 5). |
| **Buttons** | Map any of 13 physical inputs, per profile, in two independent layers (normal / Hypershift), to a keyboard key, mouse button, DPI action, profile switch, media key, lighting toggle or side-scroll. |
| **Host actions** | Anything the firmware *cannot* do — multi-step key sequences, delays, held keys, turbo, toggles, launching programs, typing text, 23 Windows commands — runs on the PC, triggered by a spare key the mouse sends. |
| **Profile settings** | DPI (per stage), DPI-clutch value, polling rate, written into the per-profile table. |
| **Device (global)** | Scroll feel (tactile / free-spin / micro), acceleration, Smart Reel, power settings — device-wide. |
| **Lighting** | Zone, effect, direction, rate, brightness, colour. |
| **Power** | Sleep timeout, low-battery warning threshold, live battery and link state. |
| **Tray** | Runs invisibly with a battery gauge in the notification area; optional start with Windows. |
| **Raw command** | Send any `class/cmd/args` triple to the device and read the reply — the tool used to discover everything above. |

**Supported hardware:** Razer Basilisk V3 Pro 35K — USB PIDs `0x00CC` (wired) and `0x00CD`
(2.4 GHz dongle). Other Razer HID products may work; see [Known limits](#known-limits).

**Requires:** Windows 10 or 11, 64-bit. No .NET installation, no Visual C++ redistributable —
the executable is a single self-contained file for the .NET Framework runtime that ships with
Windows.

---

## Install

### Option A — installer (recommended)

Download `Rz3-Setup-<version>.exe` from the [Releases](../../releases) page and run it.

The installer is **per-user and does not ask for elevation**. It:

1. copies Rz3 into `%LOCALAPPDATA%\Programs\Rz3\<version>\` — each version gets its own
   folder, so releases never overwrite one another;
2. adds a Start-menu entry (and a desktop shortcut if you tick the box);
3. optionally adds a **start-with-Windows** entry in
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` pointing at `Rz3.exe --tray`;
4. offers to close Razer Synapse for you (Rz3 and Synapse must not use the mouse at the same
   time).

It does **not** install a Windows service, and that is deliberate — see
[Why not a service?](#why-not-a-service).

Rz3 stores its own configuration in one shared, version-independent folder,
`%LOCALAPPDATA%\Rz3`, so upgrading never loses your host actions or preferences.

### Option B — portable

Copy `Rz3.exe` and `Rz3.ico` into the same folder anywhere you like and run it.

Portable mode is fully functional, but two things behave differently and the status bar will
tell you so:

- **Autostart and the tray can silently fail** if the folder you chose is one Windows treats
  as untrusted (a network drive, a synced folder, some sandboxed locations). Windows gives such
  processes a *restricted token*, and with it the registry write for autostart is refused and
  the tray icon may never register.
- **Upgrades need a manual copy**, and two copies in two folders will fight over the device.

If in doubt use the installer, or use the program's own **Install to %LOCALAPPDATA%** button
on the *Host actions* tab, which performs the same per-user install.

### First run

1. Close Razer Synapse (Rz3 detects a busy device and says so in the status bar).
2. Start Rz3. The status bar shows the device path and firmware version when connected.
3. Bind what you need. Writes are verified against the firmware's own echo, and the device is
   re-read afterwards so the table always shows what the mouse actually stores.

> **Moving the mouse between cable and dongle is safe.** The device re-enumerates with a
> different product ID; Rz3 notices the dropped handle, rescans, and reattaches.

---

## Use

Rz3 has eight tabs. The coloured banner on each tab tells you its scope:

- **green — stored in the selected profile.** Each profile can differ.
- **orange — device-wide.** One value for the whole mouse.

### Profiles

Select, activate, create, clone, rename and delete onboard profiles, save/load a profile to a
file, or reset the current profile to Razer's factory mapping. At least one profile always
remains; the firmware refuses to delete the last one.

### Buttons

Each row is one physical input. Pick an **output type** and a **value**:

- **Keyboard key** — one key plus an optional modifier (Ctrl / Shift / Alt / Win). This is also
  how Windows shortcuts are made: the mouse's keyboard interface is a real keyboard, so
  `Win+D`, `Alt+Tab`, `Ctrl+Z` and 23 other presets work with no software running at all.
- **Mouse button**, **DPI action**, **Switch profile**, **Media key**, **Lighting toggle**,
  **Side scroll**, **Scroll mode toggle**.
- **Host action N (F13…F24)** — the bridge to the *Host actions* tab.

Mapped keys are produced **by the mouse itself**, which is why they keep working when Rz3 is
closed, on another computer, or in a game that blocks injected input.

### Host actions

Some things a button can never do on its own: press three keys in sequence, wait 400 ms, type a
sentence, hold Shift while the button is held, open a program. Those run on the PC.

Rz3 writes a **trigger key** into the button instead — one of `F13`…`F24`, which Windows uses
for nothing — and turns that trigger into whatever you define:

| Kind | Example value |
|---|---|
| Run program / file / URL | `notepad.exe`, `https://example.com`, `admin:mmc.exe` |
| Send keys (sequence) | `win+r wait:400 cmd wait:300 enter`, `tab x3` |
| Type text | `Hello, world` |
| Hold keys while pressed | `ctrl` |
| Turbo (repeat while held) | `space` at 20 Hz |
| Toggle keys on / off | `shift` |
| Media key | `mute`, `next`, `play` |
| Power / system | `lock`, `sleep`, `monitoroff`, `snipping` |
| Device action (mouse) | `dpiup`, `dpicycle`, `profnext`, `lighttoggle` |
| Windows command | `taskview`, `thispc`, `cycleapps`, `closeapp`, `snipping` … (23 total) |

The **Currently sent by** column reads the device and tells you the truth: `bound: Side
button 4` means it will fire, `not bound` means you configured an action that nothing sends.
The table is stored per profile, so the same button can do different things in different
profiles.

Host actions require Rz3 to be running; the trigger key on its own does nothing harmful if it
is not.

### Profile settings and Device (global)

DPI stages, DPI-clutch value and polling rate are per profile; scroll behaviour and power
settings are device-wide. Polling rate has its own **Measure current rate** button because the
firmware echoes back any value it is given — Rz3 counts real reports per second rather than
trusting the acknowledgement.

### Raw command

Enter `class`, `cmd` and a byte list, press **Send**, and the reply is printed as hex with the
status code decoded. This is the tab that makes the project useful beyond its current feature
set: it is how every command in [`docs/PROTOCOL.md`](docs/PROTOCOL.md) was found.

---

## The core result: how to talk to the mouse

This is the transferable part of the project. Full details and evidence in
[`docs/PROTOCOL.md`](docs/PROTOCOL.md).

### 1. The control channel

The mouse exposes, next to its normal mouse collections, a vendor-defined HID collection with a
**90-byte FEATURE report, report ID 0** (usage page `0xFF00`, usage `0x02`). Every configuration
command is a HID feature write to that report; the device answers through the same report.

Framing (all offsets decimal):

```
byte  0      status        (reply)   2 = OK, 1 = busy, 3 = error, 4 = timeout, 5 = unsupported
byte  1      transaction id          0x1F used here
bytes 2-3    remaining packets
byte  4      protocol type
byte  5      data size
byte  6      command class
byte  7      command id
bytes 8-87   arguments (padded with zeroes)
byte 88      checksum: XOR of bytes 2..87
byte 89      padding
```

Every message is exactly 90 bytes; `data size` says how many of the argument bytes are
meaningful.

### 2. Making it reliable

Three details cost the most time and are the reason a naive implementation "randomly" fails:

- **The device answers one command late.** A read immediately after a write can return the
  *previous* command's reply. Rz3 discards replies whose `class`/`cmd` do not match the request
  and keeps reading, rather than accepting the first report it sees.
- **Stale reports must be drained before sending.** Because Razer's own engine may share the
  device, Rz3 drains up to six pending feature reports before each write.
- **A successful write is not proof of a successful change.** The firmware acknowledges many
  commands it does not actually implement (`status 5` for genuinely unsupported ones, `2` for
  "accepted" values it ignores). Every write is therefore verified by reading the value back
  and comparing, and anything the firmware merely echoes is treated as unverified.

### 3. The button-mapping command

This is the command Razer never documented and that makes Synapse-free remapping possible:

```
class 0x02, cmd 0x0C
args: slot(profileId), buttonId, mode(0=normal, 1=Hypershift),
      functionId, dataSize, data[dataSize]
```

`functionId` selects the output type; for a keyboard key it is `0x02` with
`data = [modifierMask, hidUsage]`, using the standard USB HID Keyboard/Keypad usage page.
Reading back is `class 0x02, cmd 0x8E` with an 80-byte payload of
`[profileId, mode, recordOffset]`; the reply lists `[buttonId, functionId, dataSize, data]`
records, nine per page.

The 13 mappable inputs and their IDs, the output-type table, the modifier bit layout, the DPI
and polling commands, the profile commands, the lighting matrix and the power commands are all
tabulated in [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

### 4. When the firmware is not enough: the trigger-key bridge

The firmware emits **one HID report per press**. This is a hard limit: no delays, no sequences,
no "hold", no launching programs. Razer solves it in a host-side engine; so does Rz3, but
openly:

```
button ──(firmware)──> F13 ──(WH_KEYBOARD_LL hook in Rz3)──> whatever you defined
```

`F13`…`F24` are real HID usages the firmware accepts, and Windows assigns them to nothing, so
they are a safe private channel per button. Rz3's hook sees the key even while another
application has focus and swallows it, so the focused program never receives it. The action
runs on a worker thread, and the hook callback only parses and queues — a slow hook callback
makes Windows silently remove the hook.

Two consequences worth knowing:

- **`WH_KEYBOARD_LL`, not Raw Input.** Raw Input with `RIDEV_INPUTSINK` does not deliver
  background key events on this machine, while a low-level keyboard hook does; Raw Input
  remains as a fallback for the rate meter.
- **The hook needs its own thread with its own message pump.** Installed on the UI thread, the
  hook stopped receiving keys the moment the window was minimised to the tray. This was one of
  the longest-running bugs in the project.

---

## Architecture

```
src/
  Program.cs      UI (WinForms), profile logic, write/verify logic, power policy, all test switches
  RzDevice.cs     HID transport: device discovery, 90-byte feature transactions, CRC,
                  reconnect logic, DPI / polling / profile / lighting / power commands
  Mapping.cs      input IDs, output types, data layouts, factory mapping table, key tables
  HostEngine.cs   trigger keys, host actions, the keyboard hook, key injection, process launching,
                  Windows command layer, action-table persistence
  RateMeter.cs    Raw Input counter used to measure the real polling rate
  TrayIcon.cs     tray icon (multi-size ICO drawn in code) and the UI preference store
  Version.cs      the single source of truth for the version number
installer/
  Rz3.iss         Inno Setup script (per-user, no service; see the header of the file)
scripts/
  build-release.ps1   version-stamped build, test run, installer, SHA-256 sums
docs/
  PROTOCOL.md     the protocol reference — the main technical deliverable
  RELEASING.md    how a new version is cut and published
```

**Separate device -> shared state -> per-version program.** Configuration that belongs to the
mouse is written to the mouse. Configuration the mouse cannot store (host actions, locally
remembered profile names, UI preferences) lives in `%LOCALAPPDATA%\Rz3`. The program itself is
installed per version. That is what lets several versions coexist and lets an uninstall leave
your settings alone.

---

## Build from source

You need Windows and nothing else — the C# compiler that ships with the .NET Framework is used
on purpose so that a contributor does not have to install a toolchain:

```powershell
git clone <this repo>
cd rz3
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

That produces `dist\Rz3.exe` and runs the logic test suite.

To also build the installer, install [Inno Setup](https://jrsoftware.org/isdl.php) and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Installer
```

The raw compiler invocation, if you want it without the script:

```
csc /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:Rz3.ico ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    /out:Rz3.exe Program.cs Mapping.cs RzDevice.cs RateMeter.cs HostEngine.cs TrayIcon.cs Version.cs
```

`/codepage:65001` is not optional: without it every non-ASCII string literal is read as ANSI
and the text turns to mojibake.

---

## Testing

The program carries its own test harness, because this project is about a device that cannot
be exercised in CI. Every switch writes a report file next to the executable.

**Pure logic, no mouse needed** (safe to run anywhere, also run automatically by the build):

```
Rz3.exe --logictest
```

**Layout and UI, no mouse needed:**

```
Rz3.exe --uicheck        # overlapping controls, clipped text, controls outside their parent
Rz3.exe --icontest       # renders the tray icon and asserts colour and coverage
```

**Device tests** (need the mouse awake; several create and delete a temporary profile):

| Switch | Purpose |
|---|---|
| `--probe` | device health check: firmware, battery, profiles, DPI, polling |
| `--selftest` | mapping write and read-back round trip |
| `--switchtest` | profile switching |
| `--dpitest` | DPI stage table, on a non-active profile, restored afterwards |
| `--ratetest <s>` | measures real reports per second |
| `--linktest`, `--linkmodetest` | cable / dongle / none, charging state |
| `--powertest`, `--rangetest` | power settings and their real accepted ranges |
| `--hosttest`, `--fire <slot>` | host action engine, hook, launcher |
| `--triggercheck`, `--watchkeys <s>` | diagnose a trigger key that does not arrive |
| `--traytest`, `--hidetotraytest`, `--trayhooktest <s>` | tray icon and engine survival while hidden |
| `--startuptest` | autostart registry path, and whether the location allows it |
| `--tabtest` | Buttons <-> Host actions consistency |
| `--wincmds [key]` | the Windows command layer |

`--uiprobe <id>` dumps the layout of one tab as text, which is useful for discussing a UI
problem without screenshots.

---

## Versioning and releases

Rz3 follows [semantic versioning](https://semver.org/):

- **MAJOR** — a change users can feel as a break: a state file stops being read, a protocol
  meaning changes, an install layout moves again.
- **MINOR** — new capability, nothing breaks.
- **PATCH** — fixes and documentation only.

The version lives in exactly one place, `src/Version.cs`. The build script reads it, stamps it
into the executable's file version, and passes the same number to the installer, so the window
title, the exe, the install folder and the Add/Remove Programs row cannot disagree. With
`-Release` the script additionally refuses to build if the git tree is dirty or if the tag
`v<version>` points at a different commit.

Each release installs into its own folder, `%LOCALAPPDATA%\Programs\Rz3\<version>`. Installing
version 1.1.0 next to 1.0.0 does not disturb 1.0.0, which is the escape hatch if a new version
misbehaves.

Release procedure: [`docs/RELEASING.md`](docs/RELEASING.md).

---

## Known limits

These are real and measured, not guesses.

- **At most 5 onboard profiles**, and the last one cannot be deleted — firmware limit.
- **Some commands are not implemented by this firmware revision**, which answers `status 5`:
  profile status/reset/colour, and debounce (so debounce can be neither read nor written).
- **Profile names** only persist on profiles that Razer Synapse originally allocated a name
  area for. For profiles Rz3 creates, the name is kept locally in `rz3_names.json` and shown
  with “(local)”. Cosmetic only.
- **Polling rate is 1000 Hz maximum on this device.** The high-speed (8000 Hz) command is
  implemented and tried first, but this dongle answers “unsupported”. The measurement counts
  reports while the mouse moves, so a slow drag shows a low average — the nominal value comes
  from the firmware table and the measurement is a sanity check.
- **Host actions require Rz3 to be running.** If it is not, the trigger key is inert — which is
  a deliberate failure mode: the button does nothing rather than something wrong.
- **A Windows service cannot host the engine.** Services run in session 0 and cannot reach the
  interactive desktop, so they cannot install a keyboard hook in your session or inject keys
  into it. The installer uses a per-user autostart entry instead.
- **Unsigned binaries.** There is no code-signing certificate for this project, so Windows
  SmartScreen will warn on first run. Verify the SHA-256 in the release notes if you care, and
  build from source if you would rather trust the code than a signature.
- **Only the Basilisk V3 Pro 35K has been tested.** Other Razer HID devices use the same
  channel and may work, but nothing beyond this one device has been verified, and firmware
  differences are exactly the sort of thing that surprises you.
- **Windows 10/11 x64 only.**
- A few output types (macro, Razer key, power keys, turbo) are accepted and read back correctly
  by the firmware, but their physical behaviour has not been press-tested.

---

## Safety

Rz3 reads and writes **HID feature reports** only. It does not touch firmware update commands,
does not reflash anything, and does not modify mouse drivers. The worst realistic mistake is
that you map a button to the wrong key, which you fix by remapping it or by using the program's
**Reset to Razer defaults** and, if you want, reinstalling Razer Synapse.

There is no bootloader or firmware-writing code in this project, and none should ever be added.

---

## License and trademarks

MIT — see [LICENSE](LICENSE).

Rz3 is an independent implementation and contains no Razer code. Some protocol framing was
cross-checked against [OpenRazer](https://github.com/openrazer/openrazer) (GPL-2.0) during
research; no OpenRazer source is included or linked.

“Razer”, “Basilisk” and “Synapse” are trademarks of Razer Inc. This project is not affiliated
with, endorsed by, or supported by Razer Inc.
