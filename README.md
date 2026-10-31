# Speaker Boost

A tiny, portable Windows app (≈32 KB) that makes quiet laptop speakers louder and
clearer — **without** the vendor's audio bloatware, and **without** touching
headphones, Bluetooth audio or monitor speakers.

---

## Problem statement

Many laptops (this project was built on an Acer with Realtek audio) ship with
**DTS:X Ultra** to make their tiny built-in speakers usable. With DTS turned off,
the speakers are very quiet and thin, because the manufacturer tuned them to be
used *through* DTS's processing (loudness compression, speaker EQ and limiting).

DTS:X Ultra, however, comes as a heavy bundle:

- a Microsoft Store app plus a background service (`DtsApo4Service`) running at all times,
- four driver packages (~1 GB in the driver store),
- a "Hardware Support App" driver that silently **reinstalls the app** after you remove it,
- features most people never use (virtual surround, game modes, personalised hearing profiles).

Removing DTS fixes the bloat but brings back the quiet speakers. Generic
equalizer apps either apply to *every* output (so headphones get blasted too),
need a background process doing the audio work, or lose their settings when a
driver update changes the speaker's device ID.

**Goal:** get DTS-like loudness on the built-in speakers only, with the smallest
possible footprint, a simple click-to-use interface, and settings that survive
driver updates.

## How it works

```
App audio ─► Windows audio engine (audiodg.exe)
                 └─ Equalizer APO   (only attached to "Speakers – Realtek")
                       ├─ Preamp +N dB          louder
                       ├─ High-pass 100–200 Hz  remove bass the speakers can't play
                       ├─ −2 dB @ 300 Hz        less boxy / muddy
                       ├─ +N dB @ 2.5 kHz       clearer voices, perceived loudness
                       ├─ +N dB shelf @ 8 kHz   detail
                       └─ LoudMax limiter       no clipping or crackling
              ─► built-in speakers
```

- The actual sound processing is done by **Equalizer APO** inside Windows' own
  audio engine. Speaker Boost only writes its configuration file.
- The boost is locked to the speakers **twice**: Equalizer APO is installed on the
  speaker device only, and the config contains a `Device:` filter with the
  speakers' unique ID. Plug in headphones and Windows switches to the separate
  Headphones device, which gets untouched audio.
- Settings are stored in the first line of the Equalizer APO config, so the app
  creates no extra files and stays portable.
- A lightweight **watchdog** (every 20 s, ~6 MB RAM) re-applies the boost if a
  driver update changes the speakers' device ID or another program overwrites
  the config.

## Prerequisites

| Requirement | Why | Notes |
|---|---|---|
| **Windows 10 or 11, 64-bit** | | |
| **.NET Framework 4.x** | Runs the app | Built into Windows 10/11 — nothing to install |
| **Built-in speakers exposed as "Speakers (Realtek…)"** | The app targets this device | Check in *Settings → System → Sound* |
| **[Equalizer APO](https://sourceforge.net/projects/equalizerapo/)** | Does the audio processing | During setup, tick **only "Speakers – Realtek(R) Audio"**, then restart |
| **[LoudMax](https://loudmax.blogspot.com/)** *(optional, recommended)* | Limiter: allows up to +12 dB without crackling | Copy the 64-bit VST2 file `LoudMax64.dll` to `C:\Program Files\VSTPlugins\`. Without it, boost is capped at +3 dB |
| **DTS:X Ultra / other audio enhancers disabled or removed** | Avoids two effects fighting each other | See [Removing DTS](#removing-dts-optional) |

Only needed to **rebuild** the app: nothing extra — `build.ps1` uses the C#
compiler that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`).

## Usage

1. Install the prerequisites above.
2. Double-click **`SpeakerBoost.exe`**. All three status lines should be green.
   If Equalizer APO is missing or not attached to the speakers, the app shows a
   button to fix it.
3. Click **Low / Medium / High**, or fine-tune with the sliders. Changes apply
   instantly — no save button, no restart.
4. Tick **Start with Windows** to have it start quietly in the tray at login.

| Preset | Volume | Voice | Treble | Bass cut |
|---|---|---|---|---|
| Low | +4 dB | +3 dB | +1 dB | 150 Hz |
| Medium | +7 dB | +4 dB | +2 dB | 150 Hz |
| High | +10 dB | +5 dB | +2 dB | 200 Hz |

### Tray and taskbar

- **Left-click / double-click** the tray icon → open the window.
- **Right-click** → Open, Boost on/off, Preset, Start with Windows, Exit.
- Hovering shows the current state, e.g. *Speaker Boost: ON (+7 dB)*.
- Closing the window (**X**) hides it to the tray; use **Exit** to quit.
  The boost stays applied even after exiting — only the watchdog stops.
- Launching the exe again opens the already-running instance.

### Command line

| Command | Effect |
|---|---|
| `SpeakerBoost.exe` | Open the window |
| `SpeakerBoost.exe /background` | Start hidden in the tray (used by *Start with Windows*) |

## Building from source

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

This draws `app.ico` (if missing) and compiles `SpeakerBoost.exe`. Close any
running copy first (tray → Exit), otherwise the exe is locked.

## Files

| File | Purpose |
|---|---|
| `SpeakerBoost.exe` | The app (portable, single file) |
| `SpeakerBoost.cs` | Source code (C# 5, WinForms) |
| `build.ps1` | Build script |
| `app.manifest` | Runs without admin, DPI-aware |
| `app.ico` | App / tray icon |
| `SpeakerBoost.ps1` | Older command-line version (`-Level Low`/`Medium`/`High`, `-Off`); not needed with the app |

## Removing DTS (optional)

DTS:X Ultra consists of an app **and** drivers. Uninstalling only the app leaves
the DTS audio effect running and lets Windows reinstall the app. Full removal:

1. Back up first: the driver folders `dtsapo4*` from
   `C:\Windows\System32\DriverStore\FileRepository`, the registry key
   `HKLM\SOFTWARE\DTS`, and the app's data folder.
2. Uninstall the **DTS:X Ultra** app (*Settings → Apps*).
3. As administrator, delete the DTS driver packages (find their `oemXX.inf`
   names with `pnputil /enum-drivers`):
   ```powershell
   pnputil /delete-driver oemXX.inf /uninstall /force
   ```
4. Restart Windows.

To get DTS back: reinstall **DTS:X Ultra** from the Microsoft Store, or install
the audio driver from your laptop manufacturer's support site.

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Equalizer APO is not attached to the speakers" | Click **Attach to speakers…**, tick only *Speakers*, restart Windows |
| "Built-in Realtek speakers not found" | Speakers are disabled or named differently; check *Sound settings* |
| "Windows blocked saving the settings" | Accept the prompt to restart the app as administrator |
| Boost capped at +3 dB | Install LoudMax (see prerequisites), then click **Refresh** |
| Sound crackles at high levels | Use a lower preset or install LoudMax |
| No boost after a big Windows / driver update | Open the app and click **Refresh**; if Equalizer APO was detached, re-attach it |
| SmartScreen warning on first run | The exe is not code-signed: *More info → Run anyway* |

## Uninstall

1. Untick **Start with Windows**, then tray → **Exit**.
2. Delete the program folder.
3. Optionally restore the previous Equalizer APO config from
   `C:\Program Files\EqualizerAPO\config\config.txt.before-speakerboost`,
   or uninstall Equalizer APO.

## License

MIT — see [LICENSE.md](LICENSE.md). Equalizer APO and LoudMax are separate
projects under their own licenses.
