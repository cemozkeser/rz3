# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

## [1.0.0] — 2026-11-20

The first version intended for use by people other than the author.

### Added

- **Wireless mouse support, end to end.** Everything is driven over the mouse's own vendor HID
  control channel: no Razer driver, no Razer service, no administrator rights.
- **Profiles** — list, activate, create, clone, rename, delete up to 5 onboard profiles;
  save/load a profile to a file; reset to Razer factory defaults.
- **Button mapping** — 13 physical inputs, per profile, in two independent layers (normal and
  Hypershift), to keyboard keys with modifiers, mouse buttons, DPI actions, profile switching,
  media keys, lighting toggles, side scroll or scroll-mode switching. Written into the mouse's
  firmware, so mappings keep working with Rz3 closed and on other computers.
- **Windows shortcut presets** — 26 Windows shortcuts (`Win+D`, `Alt+Tab`, `Ctrl+Shift+Esc`, …)
  emitted by the mouse itself, requiring no software to be running.
- **Host actions** — the layer for everything the firmware cannot do: key sequences with delays,
  typed text, held keys, turbo, toggles, launching programs/documents/URLs, 23 Windows commands.
  Driven by `F13`…`F24` trigger keys the firmware emits per button.
- **Per-profile settings** — DPI stages, DPI-clutch value, polling rate.
- **Device-wide settings** — scroll feel (tactile / free-spin / micro), acceleration, Smart Reel,
  lighting (zone, effect, direction, rate, brightness, colour), power (sleep timeout,
  low-battery threshold).
- **Tray mode** — battery gauge drawn in the icon, optional battery tooltip, optional start with
  Windows, single-instance enforcement.
- **Raw command tab** — send any `class/cmd/args` triple and read the reply, which is how the
  protocol was discovered and how it can be extended.
- **Self-test harness** — 20+ command-line switches covering logic, layout, icon rendering, the
  device, the host engine, the tray and autostart.
- **Installer** — per-user, no elevation, side-by-side version installs, optional autostart,
  optional Razer Synapse shutdown, uninstall that can keep or remove configuration.
- **Documentation** — [`docs/PROTOCOL.md`](docs/PROTOCOL.md), the full protocol reference for the
  Basilisk V3 Pro 35K, and [`docs/RELEASING.md`](docs/RELEASING.md).

### Fixed

- The window/tray icon fell back to an in-code drawing after installation because `Rz3.ico` was
  not copied alongside the executable.
- Shift-on-close is now honoured: closing the window parks Rz3 in the tray unless Shift is held,
  which matches the documented behaviour.
- Unexpected errors now write a full stack trace to `rz3_crash.log` instead of showing a bare
  message box, so a bug report from another machine carries evidence.
- The installed copy no longer forgets `rz3_power.json` during install.
- **Key sequences with a spaced repeat count now work.** `tab x3` — the exact form the UI hint
  and the docs have always advertised — used to fail with `unknown token: x3`, because the
  sequence is split on spaces and the repeat suffix was accepted only when glued to its key
  (`tabx3`). Both spellings work now, and a test pins it.

### Known limits at this version

See the “Known limits” section of the [README](README.md). The most important ones: one device
model tested, unsigned binaries (SmartScreen warning), host actions need Rz3 running, 1000 Hz
polling ceiling on this hardware, and some firmware commands simply do not exist on this
revision and are reported as unsupported rather than faked.

[Unreleased]: https://github.com/cemozkeser/rz3/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/cemozkeser/rz3/releases/tag/v1.0.0
