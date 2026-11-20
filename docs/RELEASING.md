# Releasing Rz3

This is the checklist for publishing a new version. Follow it in order; each step exists because
skipping it caused a problem at least once.

---

## 0. The mental model

Three things must agree, and the build script enforces that they cannot drift:

```
src/Version.cs  ──>  Rz3.exe file version  ──>  Rz3-Setup-<version>.exe
       ^
       └── git tag v<version>   (checked by build-release.ps1 -Release)
```

The version lives in **one** place: `src/Version.cs`. Never edit a version string anywhere else.

---

## 1. Decide the version

Rz3 follows [semantic versioning](https://semver.org/). Bump…

| Change | Bump |
|---|---|
| Fixed a bug, improved a message, updated docs | **PATCH** (`1.0.0` → `1.0.1`) |
| New feature that breaks nothing | **MINOR** (`1.0.1` → `1.1.0`) |
| Something users can feel as broken: config no longer read, protocol meaning changed, install layout moved again, a supported device dropped | **MAJOR** (`1.1.0` → `2.0.0`) |

Edit `src/Version.cs`:

```csharp
public const string Version = "1.0.1";
```

---

## 2. Test before you publish

Run the parts that do not need hardware anywhere, then the device tests with the mouse connected
and Razer Synapse closed.

```powershell
# logic: enums, triggers, chords, persistence, version plumbing  (also runs in the build)
.\dist\Rz3.exe --logictest

# layout: overlapping/clipped controls
.\dist\Rz3.exe --uicheck

# icon rendering
.\dist\Rz3.exe --icontest

# device (mouse connected, Synapse closed)
.\dist\Rz3.exe --probe
.\dist\Rz3.exe --selftest
.\dist\Rz3.exe --dpitest
```

`--logictest` must print `fail=0`. `--uicheck` must print `issues=0`.

If you touched anything on the Host actions path, also run `--hosttest`, `--fire 0` and
`--tabtest`. If you touched the tray, run `--traytest` and `--hidetotraytest`.

---

## 3. Update the docs

- `README.md` — the "What it does" table if you added a capability, and "Known limits" if you
  found a new one.
- `docs/PROTOCOL.md` — **any new or corrected command goes here with its date and the evidence**.
  An unverified command must be marked *unverified*. This file is the reason the project is worth
  publishing; treat it as the primary deliverable, not an appendix.
- `CHANGELOG.md` — add an entry under the new version.

---

## 4. Commit

```powershell
git add -A
git commit -m "Rz3 1.0.1: <one line about what changed>"
```

`-Release` refuses to build on a dirty tree, so this step is mandatory.

---

## 5. Tag

```powershell
git tag v1.0.1
```

The tag name **must** be `v` + exactly the number in `src/Version.cs`. `-Release` checks that the
tag for this version points at `HEAD`, and that `HEAD` is not already tagged with a different
version.

---

## 6. Build the release

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Release -Installer
```

This will:

1. read the version from `src/Version.cs`;
2. refuse to continue if the tree is dirty or the tags disagree;
3. compile with the version stamped into the exe's file version and generate SHA-256 sums;
4. run `--logictest` on the **built** binary and abort if anything fails;
5. compile the installer from the same version number;
6. write `dist\SHA256SUMS.txt`.

Requires [Inno Setup](https://jrsoftware.org/isdl.php) for step 5. Output:

```
dist\Rz3-Setup-1.0.1.exe
dist\Rz3.exe
dist\Rz3.ico
dist\SHA256SUMS.txt
```

Verify the installer before publishing:

```powershell
# install it, start it, check the tray icon and one host action
.\dist\Rz3-Setup-1.0.1.exe
# then confirm the version in the window title, then uninstall from
# Settings > Apps and check that the shared %LOCALAPPDATA%\Rz3 folder is preserved
```

---

## 7. Push and publish on GitHub

```powershell
git push origin main
git push origin v1.0.1
```

Then on GitHub: **Releases → Draft a new release**, choose the tag `v1.0.1`, title it
`Rz3 1.0.1`, and attach the three files from `dist\`. Paste the changelog section into the
description and include the SHA-256 sums so people can verify what they downloaded.

Mark it **"Latest release"** so the `/releases/latest` link used by the README stays correct.

---

## 8. If something is wrong after publishing

Do not delete a release people may already have. Publish a PATCH release that fixes it, and edit
the broken release's description to point at the replacement.

Because each version installs to `%LOCALAPPDATA%\Programs\Rz3\<version>`, a user can simply run
the older installer and start that version instead — the old files are still there unless
"Remove older installed versions" was chosen.

---

## First-time GitHub setup

Only needed once, when the repository is created:

```powershell
# from the folder that contains this README
git init
git add -A
git commit -m "Rz3 1.0.0: first public version"
git branch -M main
git tag v1.0.0
git remote add origin https://github.com/<user>/<repo>.git
git push -u origin main
git push origin v1.0.0
```

Then set on the repository page:

- **About** → description: “Complete Synapse-free replacement for the Razer Basilisk V3 Pro 35K,
   with the HID control protocol documented.”
- **About** → topics: `razer`, `basilisk`, `hid`, `reverse-engineering`, `windows`,
  `no-synapse`, `hardware`, `csharp`
- **Settings → General → Releases**: enable “Automatically delete head branches” if you use them.
- Pin the release so visitors land on the download.
