# Rz3 protocol reference

Everything on this page was **measured on the device**, not inferred from documentation, and
every entry carries the date it was confirmed. Where something is unverified it is marked
**unverified** — do not build a feature on an unverified line without testing it.

Target: **Razer Basilisk V3 Pro 35K**. USB PIDs `0x00CC` (wired) and `0x00CD` (2.4 GHz dongle).
Both speak the same protocol; `0x00CC` also accepts `0x00D6` / `0x00D7` in Rz3's device list for
sibling models.

> Practical advice before anything else: Razer Synapse and Rz3 cannot use the device at the same
> time. Close Synapse. Rz3 does not need administrator rights, and does not need Synapse to be
> uninstalled — only closed.

---

## Contents

1. [Finding the control channel](#1-finding-the-control-channel)
2. [Transaction framing](#2-transaction-framing)
3. [Reliability rules](#3-reliability-rules)
4. [Status codes](#4-status-codes)
5. [Button mapping](#5-button-mapping)
6. [Mappable inputs](#6-mappable-inputs)
7. [Output types](#7-output-types)
8. [Profiles](#8-profiles)
9. [DPI](#9-dpi)
10. [Polling rate](#10-polling-rate)
11. [Scroll](#11-scroll)
12. [Lighting](#12-lighting)
13. [Power and battery](#13-power-and-battery)
14. [What is per-profile and what is global](#14-what-is-per-profile-and-what-is-global)
15. [Commands this firmware does not implement](#15-commands-this-firmware-does-not-implement)
16. [How the protocol was found](#16-how-the-protocol-was-found)
17. [Reproducing the write+verify loop](#17-reproducing-the-writeverify-loop)

---

## 1. Finding the control channel

The mouse enumerates as several HID collections. The one that matters is a vendor-defined
collection with:

| Property | Value |
|---|---|
| Usage page | `0xFF00` (vendor-defined) |
| Usage | `0x02` |
| Feature report length | **90 bytes** |
| Report ID | **0** |

The mouse collection itself (usage page `0x01`, usage `0x02`) carries 5 buttons, an 8-bit wheel
and 16-bit X/Y. It has **no AC Pan field**, which is why horizontal scrolling (tilt) cannot be
read as an axis — see [§6](#6-mappable-inputs).

Discovered by enumerating HID device interfaces (`SetupDiGetClassDevs` with the HID interface
GUID), filtering on `vid_1532`, then asking each interface for its preparsed data and reading
`HidP_GetCaps`:

```
if (st != HIDP_STATUS_SUCCESS) skip;       // 0x110000
if (caps.FeatLen < 90)       skip;         // only the vendor collection has 90-byte features
```

That single test is enough to identify the control channel on any Razer HID device.

---

## 2. Transaction framing

A command is one `HidD_SetFeature` call with a 91-byte buffer: report ID `0x00` followed by the
90-byte message. The reply arrives by `HidD_GetFeature` with the same 91-byte shape.

```
       0   1   2   3   4    5    6      7        8 .. 87      88    89
     +---+---+---+---+----+----+-----+------+----------------+-----+----+
     | st| tx| rp| rp| pt | ds | cls | cmd  |    arguments   | crc | pad|
     +---+---+---+---+----+----+-----+------+----------------+-----+----+
```

| Offset | Field | Notes |
|---|---|---|
| 0 | status | reply only — see [§4](#4-status-codes). Requests send `0x00`. |
| 1 | transaction id | `0x1F` is what Rz3 uses; the device echoes it. |
| 2-3 | remaining packets | always `0`, no multi-packet commands are used. |
| 4 | protocol type | `0`. |
| 5 | data size | number of meaningful argument bytes. |
| 6 | command class | see the class/cmd tables below. |
| 7 | command id | `0x80..0x8F` read, `0x00..0x0F` write, `0x40..0x4F` some writes. |
| 8-87 | arguments | zero-padded to 80 bytes. |
| 88 | checksum | XOR of bytes `2..87`. |
| 89 | padding | `0`. |

The argument area is always 80 bytes even when only one byte is meaningful. **Header lengths
matter:** several commands reject a payload shorter than the firmware expects with
`status 3` rather than padding it for you.

---

## 3. Reliability rules

These are the three rules that turn "works sometimes" into "works":

### 3.1 The device answers one command late

After a write, the first `GetFeature` may return the **previous** command's reply. Never accept
the first matching-shape report. Compare `class` and `cmd` against what you sent and keep
reading:

```csharp
for (int attempt = 0; attempt < 120; attempt++) {
    HidD_GetFeature(h, resp, 91);
    body = resp[1..90];
    bool matches = body[6] == commandClass && body[7] == commandId;
    if (matches && body[0] == 0x01) { Thread.Sleep(20); continue; }  // busy, keep waiting
    if (matches) return body;
    Thread.Sleep(15);
}
```

### 3.2 Drain stale reports before sending

Razer's own engine may leave pending feature reports in the queue. Before each write, drain up to
six reports:

```csharp
for (int i = 0; i < 6; i++)
    if (!HidD_GetFeature(h, sink, 91)) break;
```

### 3.3 An acknowledgement is not proof of a change

This firmware acknowledges commands it does not implement, and echoes back values it does not
store. Two concrete examples found the hard way:

- `00/0E` (polling) echoes `status 2` for **any** id from 1 to 16, including ids that are not in
  the rate table and produce no measurable rate.
- Profile-name writes on slots Rz3 created return `status 2` but the value is not stored — the
  read-back is empty.

So: **write, then read back and compare**, and treat a mismatch as a failure. For polling rate
specifically, Rz3 additionally counts real HID reports per second, because the nominal value in
the firmware table is not evidence that the mouse is really running at that rate.

### 3.4 Handle loss and re-enumeration

The handle dies when the cable is pulled, the mouse sleeps, or it is moved between cable and
dongle. On the last case the **product ID changes** (`0x00CC` ↔ `0x00CD`), so the old interface
path is gone permanently and reopening it can never succeed. Recovery is: close, re-scan the
device list, and reopen by *new* path. Retrying the stored path forever is a hang.

Relevant Win32 errors on the HID handle: `1167` (ERROR_DEVICE_NOT_CONNECTED),
`6` (ERROR_INVALID_HANDLE).

---

## 4. Status codes

| Value | Meaning |
|---|---|
| `0x02` | success — arguments are echoed back |
| `0x01` | busy — retry shortly |
| `0x03` | error, or wrong argument shape |
| `0x04` | timeout — typically the device is asleep; wake it by moving the mouse |
| `0x05` | unsupported by this firmware |
| `0x00` | meaning not verified |

`0x04` deserves emphasis: a wireless body that is idle returns timeouts, not errors. Polling code
must treat it as "asleep", not as a broken device — and any device read that fails with `0x04`
should be retried once before the UI declares the device missing.

---

## 5. Button mapping

This is the command that makes Synapse-free remapping possible.

### 5.1 Write

```
class 0x02, cmd 0x0C
args: slot, buttonId, mode, functionId, dataSize, data[dataSize]
```

- `slot` — the profile id to write into (profile ids are `1..5`).
- `buttonId` — see [§6](#6-mappable-inputs).
- `mode` — `0` normal, `1` Hypershift.
- `functionId` — see [§7](#7-output-types).
- `dataSize` / `data` — depends on `functionId`.

A **disabled** mapping is not a function id: send only three argument bytes
`[slot, buttonId, mode]` with no `functionId`. The record shrinks to three bytes.

Writing into a profile that is **not active works** — no activation round-trip is needed. This
was verified directly.

### 5.2 Read

```
class 0x02, cmd 0x8E
args: 80 bytes, [profileId, mode, recordOffset, 0 ...]
```

The reply repeats that header and then lists records:

```
[buttonId, functionId, dataSize, data(4 bytes, padded)]
```

**Nine records fit in one page.** Page through with the offset argument: offset `0` and offset `9`
together return all 13 inputs.

A partial read is dangerous: rows you did not receive look like "disabled", and writing that
table back would really disable them. Rz3 tracks whether the read covered every record and
refuses to offer a write of an incomplete table.

---

## 6. Mappable inputs

The device reports its own list — `class 0x02, cmd 0x84` returns
`0d | 01 02 03 04 05 34 35 60 0e 0f 6a 09 0a` (`0x0d` = 13 buttons, then the ids):

| ID | Input | Notes |
|---|---|---|
| `0x01` | Left click | |
| `0x02` | Right click | |
| `0x03` | Wheel click | |
| `0x04` | Side button 4 | |
| `0x05` | Side button 5 | |
| `0x09` | Scroll up | |
| `0x0A` | Scroll down | |
| `0x34` | Tilt left | horizontal scroll |
| `0x35` | Tilt right | horizontal scroll |
| `0x60` | Top button 1 | free-spin button |
| `0x6A` | Top button 2 | |
| `0x0E` | Bottom profile button | |
| `0x0F` | Clutch / sniper | |

### Why tilt cannot be read as an axis

An earlier hypothesis was that tilt appears as an AC Pan field. It does not: the mouse
collection has no such field. Tilt is emitted the way **firmware mapping says**. If tilt is
mapped to side-scroll, Windows receives `WM_MOUSEHWHEEL`; if it is mapped to a key, the mouse's
keyboard interface emits that key. Consequence: a host-side program cannot reliably tell tilt
apart from the side buttons by reading the mouse, because in button mode **both produce the same
`WM_XBUTTONDOWN` bytes**. The only workable separation is on the device side, by giving tilt its
own output type.

Windows virtual keys `49`/`50` on the side buttons are conditional too — they appear only when
the profile happens to map them that way. Never rely on them.

---

## 7. Output types

The `functionId` byte of the mapping record. Values come from Razer's own protocol module
(`Mapping_Assignment` enum) and were confirmed by write + echo on the device.

| functionId | Name | data |
|---|---|---|
| `0x00` | Off | none — record shrinks to 3 bytes |
| `0x01` | Mouse button | 1 byte: button id |
| `0x02` | Keyboard key | 2 bytes: `modifierMask, hidUsage` |
| `0x03` | Macro type I | 1 byte: macro number |
| `0x06` | DPI | 5 bytes: `05, x16hi, x16lo, y16hi, y16lo` |
| `0x07` | Profile navigation | 1 byte: `03` previous, `04` next |
| `0x08` | Lighting | 1 byte: `01` toggle, `02` on, `03` off — **unverified** |
| `0x09` | Power keys | 1 byte: e.g. `4C` sleep — **unverified** |
| `0x0A` | Media keys | 2 bytes: `00, consumerUsage` |
| `0x0B` | Double click | 1 byte — **unverified** |
| `0x0C` | Mode button | 1 byte — **unverified** |
| `0x0D` | Turbo mode key | 1 byte — **unverified** |
| `0x0E` | Side scroll | 3 bytes: `68,0,14` left / `69,0,14` right |
| `0x0F` | Macro type IV | 1 byte — **unverified** |
| `0x11` | Razer key | 1 byte: RAZER_KEY code — **unverified** |
| `0x12` | Scroll mode switch | 1 byte: `01` |

### Modifier mask

Standard HID modifier byte:

| Bit | `0x01` | `0x02` | `0x04` | `0x08` | `0x10` | `0x20` | `0x40` | `0x80` |
|---|---|---|---|---|---|---|---|---|
| Key | L Ctrl | L Shift | L Alt | L Win | R Ctrl | R Shift | R Alt | R Win |

### Keyboard usages

Standard USB HID Keyboard/Keypad page: `a` = `0x04` … `z` = `0x1D`, `1` = `0x1E` … `0` = `0x27`,
`Enter` = `0x28`, `Esc` = `0x29`, `F1` = `0x3A` … `F12` = `0x45`, `F13` = `0x68` … `F24` = `0x73`.
Consumer page: volume up `0xE9`, volume down `0xEA`, mute `0xE2`, play/pause `0xCD`, next `0xB5`,
previous `0xB6`, stop `0xB7`.

### Windows shortcuts without any software

Because the mask and usage are standard, `functionId 0x02` produces real Windows shortcuts:
`Win+D` is `[0x08, 0x07]`, `Alt+Tab` is `[0x04, 0x2B]`, `Ctrl+Z` is `[0x01, 0x1D]`. These are
handled by Windows itself, so they work with Rz3 closed and on any machine.

**The hard limit:** one HID report per press. No sequences, no delays, no holding. That is the
entire reason the host-action layer exists.

### The F13…F24 trigger trick

Usages `0x68..0x73` (F13…F24) are accepted by the firmware and assigned by Windows to nothing.
That makes them a **private per-button channel**: map a button to F13 and a PC-side program can
claim that F13 for anything at all, without any risk of colliding with a real shortcut. Rz3 uses
twelve of them as host-action triggers.

---

## 8. Profiles

The profile API is **`class 0x05`**. Payload lengths are strict — a short payload is rejected
with `status 3`.

| Operation | class/cmd | Payload | Result |
|---|---|---|---|
| Get number of profiles | `05/80` | 1 byte | ✅ |
| Get profile id list | `05/81` | 80 bytes | ✅ returns `max, id…` |
| Create profile | `05/02` | 1 byte (id) | ✅ max 5; a 6th is refused; same id again = no-op |
| Delete profile | `05/03` | 1 byte (id) | ✅ the last profile cannot be deleted |
| Get active profile | `05/84` | 1 byte | ✅ |
| Set active profile | `05/04` | 1 byte | ✅ |
| Get max profiles | `05/8A` | 1 byte | ✅ returns `5` |
| Get profile name | `05/88` | 80 bytes `[id, off, off, sizeHi, sizeLo]` | ✅ |
| Set profile name | `05/08` | 80 bytes `[id, off, off, sizeHi, sizeLo, blob]` | ✅ with a caveat |

### The profile-name trap

The name area exists only on profiles **Razer Synapse created**. Writing the name of a profile
Rz3 created through `05/02` returns `status 2` but is not stored — the read-back is empty. Rz3
therefore keeps such names locally and shows them with “(local)”.

Also: the size field must be the **real blob length** (16-byte GUID + name, i.e. 16 + name
chars). Writing it as the 250-byte (`0xFA`) area size corrupts the slot.

### Edge cases

- The last profile cannot be deleted — the device refuses.
- Deleting the active profile makes the device activate another one.
- Profile 1 is the factory slot and has no name area at all.
- The bottom profile button carries the "switch profile" action; `04` = next, `03` = previous.
  The `05` / `06` cycle variants and fixed-target `[02, profileId]` are accepted by the firmware
  but the wrap-around behaviour is **unverified**.

---

## 9. DPI

The critical thing to understand: **DPI lives in a per-profile class table indexed by the profile
id itself.** Class index `0` is only the volatile mirror of whichever profile is currently
active. Writing `0` therefore looks like it works and then silently reverts on the next profile
switch. This exact bug cost several sessions.

| Command | Meaning | Arguments |
|---|---|---|
| `04/86` | read the profile's DPI class table | `[profileId]`, 80 bytes |
| `04/06` | write the DPI class table | `[profileId, activeStage, count, records…]` |
| `04/85` | read the active stage's live value | `[profileId, 0,0,0,0,0,0]` (7 bytes) |
| `04/05` | write the active stage's value | `[profileId, xHi, xLo, yHi, yLo, zHi, zLo]` |
| `04/80` | read the active stage number | — |

Record layout inside the table (7 bytes each):

```
[stageId, xHi, xLo, yHi, yLo, zHi, zLo]
```

Header: `[profileId, activeStage, count]`.

A freshly created profile already has a table (400 / 800 / 1600 / 3200 / 6400), so a write works
in any profile. Writing the profile id (not `0`) makes the value survive profile changes,
closing the handle and reopening, and a new process. No activation is required.

---

## 10. Polling rate

Polling is **also per-profile**, addressed by profile id. The device-level commands
(`00/85`, `00/05`) only ever read or write the *active* profile, which is why using them
appeared to work and then confused everyone.

| Command | Meaning | Arguments |
|---|---|---|
| `00/8E` | read polling of a profile | `[profileId]` → `[profileId, rateId]` |
| `00/0E` | write polling of a profile | `[profileId, rateId]` |

Standard rate table (verified):

| Rate | id | Rate | id |
|---|---|---|---|
| 1000 Hz | `1` | 200 Hz | `5` |
| 500 Hz | `2` | 125 Hz | `8` |
| 333 Hz | `3` | 100 Hz | `10` |
| 250 Hz | `4` | | |

High-speed (HyperPolling) table, `02/C0` read / `02/40` write with `[profileId, rateId]`:

| Rate | id | Rate | id |
|---|---|---|---|
| 8000 Hz | `1` | 1000 Hz | `8` |
| 4000 Hz | `2` | 500 Hz | `16` |
| 2000 Hz | `4` | 250 Hz | `32` |
| | | 125 Hz | `64` |

**This unit answers `0x05` (unsupported) to `02/C0`**, so its ceiling is 1000 Hz. The code tries
high speed first and falls back, so a device that supports it needs no code change.

**Measuring the real rate:** the HID input pipe cannot be read directly (the mouse collection is
owned by `mouclass`, `ReadFile` fails with error `5`), so Rz3 counts `WM_INPUT` reports from
Raw Input. That counts only while the mouse is **moving**, so a slow drag reports a low average.
Do not derive a peak rate from the shortest inter-report gap either: Windows batches
`WM_INPUT` deliveries and produces ~20 µs gaps that would imply 50 kHz. The measurement is a
sanity check; the nominal value comes from this table.

---

## 11. Scroll

All scroll settings are **global** (not per profile), and all verified live on `0x00CC`. Each is
a 2-byte `[01, value]` payload.

| Setting | read | write | values |
|---|---|---|---|
| Scroll mode | `02/94` | `02/14` | `0` tactile, `1` free spin, `2` micro tactile |
| Acceleration | `02/96` | `02/16` | `0`/`1` on this device, `2` also accepted |
| Acceleration strength | `02/A2` | `02/22` | numeric |
| Smart Reel threshold | `02/A3` | `02/23` | numeric |
| Smart Reel automatic switch | `02/97` | `02/17` | `1` = on |

Notes that matter:

- **Smart Reel is not a mode.** It is the automatic switch from tactile/micro to free spin. It is
  greyed out in free spin and skipped when applying.
- If a button is bound to the scroll-mode switch (`functionId 0x12`), the firmware ignores Smart
  Reel. The UI warns and can clear those bindings.
- `02/30` (a separate tactile/free/micro command) and `02/95` (debounce read) answer `0x05` on
  this firmware; debounce writes (`02/15`) are refused, so debounce can be neither read nor
  written.
- Acceleration strength and Smart Reel threshold accept `0..4`; larger values are clamped.

---

## 12. Lighting

**Effects are device-wide** in this firmware: switching profile does not change lighting.
Effect id `0x0F/0x02`; accepted ids on `0x00CC`: `0,1,2,3,4,5,8`. Ids `6,7,9` return `0x05`.

| Effect id | Meaning |
|---|---|
| `0` | off |
| `1` | static (RGB required) |
| `2` | breathing |
| `3` | spectrum |
| `4` | wave |
| `5` | accepted, meaning **unverified** (reactive/fire family) |
| `8` | custom frame |

A custom frame uses a 13-column matrix (`2C/0F`). Brightness is global. Separate brightness for
battery mode (`0F/83`) answers `0x03` — not supported.

---

## 13. Power and battery

| Command | Meaning | Notes |
|---|---|---|
| `07/80` | battery level | percent |
| `07/81` / `07/01` | low battery threshold read / write | stores `5..100` exactly |
| `07/83` / `07/03` | time to sleep read / write | stores `10..65535` seconds exactly |
| `07/84` | **time to sleep on battery mode** | returns `00 00` — this is *not* charging state |
| `07/85` | device charging status | answers `0x05`, **not supported** |
| `07/86` | battery real ADC value | answers `0x05`, **not supported** |
| `07/8D` | power profile | not stored by this firmware (see below) |

### The `07/84` trap

`07/84` was believed to be charging status and produced a permanently-wrong "charging" tooltip.
Razer's own protocol module names it **"Get Time to Sleep Mode on Battery Mode"** — it is a sleep
duration. Charging state is `07/85`, which this firmware does not implement. Rz3 now derives the
charging indication from the link/level combination and says so instead of pretending.

### The power profile trap

The power profile is **not stored** by this firmware: writing it appears to succeed and then
reads back as "power save" every time. Rz3 probes support once per connection and hides the
control when unsupported, so a restart can never look like a reset.

### Link detection

Cable, 2.4 GHz dongle and absent can be distinguished (by product ID and interface set), and the
charge state is the **mouse's** state, not the PC's.

---

## 14. What is per-profile and what is global

| Setting | Scope |
|---|---|
| Mappings (both layers), DPI, polling, debounce, profile name | **per profile** |
| Scroll mode / acceleration / strength / Smart Reel, lighting, low-battery threshold, sleep time | **global** |

Rz3 reflects this in its UI: green banner = per profile, orange banner = device-wide.

Opening the program **does not change any setting** — nine settings were read before and after a
launch cycle and all were identical. Rz3 only reads at startup. Any future feature must keep this
property, because "the app resets my mouse" is the worst possible bug for a configuration tool.

---

## 15. Commands this firmware does not implement

These return `0x05` or `0x03` on this device and should not be presented as working:

- Profile status (`05/89`), profile reset (`05/8B`), profile colour (`05/8C`)
- Debounce read (`02/95`) and write (`02/15`)
- Charging status (`07/85`), battery ADC (`07/86`)
- High-speed polling (`02/C0`, `02/40`)
- Separate tactile/free-spin/micro command (`02/30`)
- Battery-mode separate brightness (`0F/83`)
- Profile names on slots Rz3 created (accepted, not stored)

`05/02` creating a 6th profile is refused, so 5 is the real maximum.

---

## 16. How the protocol was found

Documented so the method can be reused on other devices, because the method generalises better
than the byte tables do:

1. **Capture what the vendor tool actually sends.** Razer's feature writes were captured with
   USBPcap while changing a mapping in Synapse. A feature write is small, self-contained and
   replayable, so capture → replay → observe is a complete loop.
2. **Read the vendor's own protocol module.** Synapse ships a JavaScript module enumerating 1477
   command headers with their `class/cmd` pairs and enums (`functionId`, `buttonId`,
   `mouseAssignment`, Chroma effect ids). That names the commands and gives candidate values.
3. **Read the vendor's own log.** `%LOCALAPPDATA%\Razer\RazerAppEngine\User Data\Logs\protocol.log`
   lists the `class/cmd`/argument combinations Synapse really used, in order, with the product id.
4. **Sweep the get side.** Send every `class 0x00..0x2F` / `cmd 0x80..0x8F` read and record which
   answer with `status 2`. This maps the device's actual capability surface in a few minutes.
5. **Write, then echo-verify.** For every candidate write, read the value back and compare. This
   is what separates "accepted" from "implemented".
6. **Find the addressing, not just the command.** The two most expensive bugs on this project were
   not wrong command bytes but wrong *addresses*: DPI and polling written to the class-0 mirror
   instead of the per-profile class, which made every change look temporary.

### Traps that cost real time

- **Tilt is not an axis.** See [§6](#6-mappable-inputs).
- **Side buttons do not reliably send VK 49/50.** Whether they do is a property of the profile.
- **A low-level keyboard hook must run on its own thread with its own message pump.** Installed on
  a UI thread, it stops receiving events when the window is minimised. The symptom is "my buttons
  stop working when Rz3 is in the tray".
- **A hook callback must be fast.** Windows removes a hook whose callback exceeds
  `LowLevelHooksTimeout` (300 ms default). Parse and queue in the callback; do the work on a
  worker thread. Rz3 also raises that timeout to 5 s, per user, no admin needed.
- **Raw Input with `RIDEV_INPUTSINK` did not deliver background key events on the test machine**,
  while `WH_KEYBOARD_LL` did. Keep Raw Input as a fallback, not as the primary path.
- **Services cannot do this.** Session 0 has no interactive desktop, so a service cannot install a
  keyboard hook in your session or inject into it. The correct design is a per-user autostart.
- **Restricted tokens break shell integration.** A process started from an untrusted folder gets a
  restricted token; `HKCU\...\Run` writes then throw `SecurityException` and the tray icon may
  never register. Install under `%LOCALAPPDATA%`.

---

## 17. Reproducing the write+verify loop

The fastest way to explore the device without building anything: use the **Raw command** tab of
Rz3, or `tools/rzsend.cs` if you are writing your own probe.

Example — map tilt-left in profile 1 to the `Q` key:

```
class 0x02, cmd 0x0C, dataSize 10
args: 01 34 00 02 02 00 14 00 00 00
      |  |  |  |  |  |  |  |  |  |
      |  |  |  |  |  |  |  |  |  +-- padding
      |  |  |  |  |  |  +-- HID usage 0x14 = Q
      |  |  |  |  |  +-- modifier mask 0
      |  |  |  |  +-- extra byte
      |  |  |  +-- functionId 0x02 = keyboard
      |  |  +-- mode 0 = normal layer
      |  +-- buttonId 0x34 = tilt left
      +-- slot 1 = profile 1
```

Expected reply: `status 0x02` with the same argument bytes echoed.

Then verify by reading back with `02/8E` and looking for `[34, 02, 02, 00, 14]`.

Two safety notes:

- Only feature reports are used. Nothing here touches firmware-update or bootloader commands.
- The realistic worst case is a wrong mapping, which **Reset to Razer defaults** (or Synapse, if
  installed) fixes. There is no command in this document that can brick the device.
