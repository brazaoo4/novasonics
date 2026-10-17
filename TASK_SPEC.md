# Project Specification: NovaSonics (Lightweight SteelSeries GG & Sonar Replacement)

## 1. Objective & Scope
Build a standalone, ultra-fast native Windows 11 desktop & system tray application (**NovaSonics**) for the **SteelSeries Arctis Nova 7 Wireless** headset that completely replaces **SteelSeries GG / Sonar**[cite: 1, 2, 3]. 

Once Phase 1 (Reverse Engineering & Asset Extraction) is complete, **SteelSeries GG will be permanently uninstalled**. Therefore, this application must **not** depend on `SteelSeriesGG.exe`, `SteelSeriesSonar.exe`, or SteelSeries virtual audio drivers at runtime.

### Core Functional Requirements
1. **100% Standalone Hardware ChatMix Wheel Support:** Read the physical ChatMix dial on the Arctis Nova 7 over USB HID and dynamically balance the volume between `GAME` and `CHAT` applications in real time, synced with an on-screen `CHATMIX` slider[cite: 1, 3].
2. **Sonar-Style Mixer & Drag-and-Drop App Routing:** Replicate the Sonar Mixer interface with `MASTER`, `GAME`, `CHAT`, and `MIC` vertical strips, real-time cyan audio peak meters inside the slider tracks, active app pills (e.g., `[🎧 SPOTIFY]`), drag-and-drop movement between channels, and automatic app categorization (Game vs. Chat)[cite: 1, 3].
3. **Backup Device Auto-Switch (Priority List):** Replicate Sonar's Device Manager with a drag-and-drop `OUTPUT DEVICE PRIORITY LIST` (e.g., `1. Headphones (Arctis Nova 7)`, `2. L29w-30 (NVIDIA High Definition Audio)`, `3. Speakers (Realtek(R) Audio)`) and `EXCLUDED DEVICES`[cite: 2]. Automatically switch Windows default audio when the Nova 7 powers on/off or at boot/reboot[cite: 2].
4. **30% Low-Battery Pre-Beep Alert:** Poll battery status over USB HID and fire a native Windows Toast Notification at **30% battery** (before the headset's internal firmware triggers its unmutable 20–25% hardware beep).
5. **Hardware Settings & 1-Click Backup/Restore:** Configure hardware Sidetone, Inactive Sleep Timer, and Mic Mute LED over USB HID, and store all routing rules, priority lists, and volumes in a portable `config.json` with Export/Import buttons.

---

## 2. Phase 1: Reverse Engineering via REA MCP (Do This BEFORE Uninstalling GG)

Use the **Reverse Engineer Anything (`rea`)** MCP tools to inspect the existing SteelSeries GG installation at `C:\Program Files\SteelSeries\GG` and `C:\ProgramData\SteelSeries\GG`. Extract and document the following in `REA_FINDINGS.md`:

### Task 1.1: Extract USB HID Protocol for Arctis Nova 7
Inspect `SteelSeriesEngine.exe`, device plugin DLLs, and Electron ASAR bundles (`C:\Program Files\SteelSeries\GG\apps\engine` and `sonar`) for Vendor ID `0x1038` and Product IDs `0x2202` (Nova 7 Gen 1), `0x227E`, and `0x22A1` (Nova 7 Gen 2):
1. **ChatMix Enable & Read Reports:** 
   - Identify the exact HID Feature/Output report sent by GG/Sonar to initialize/unlock software ChatMix reporting on the Nova 7 dongle (Usage Page `0xFFC0`, Interface `3`).
   - Identify the HID Input/Output report opcode (typically `0x45` or interrupt bytes on the status report) that returns the real-time position of the physical ChatMix dial on the headset earcup (`0` to `100` or separate Game/Chat attenuation bytes).
2. **Battery & Connection State Opcode (`0xB0`):**
   - Verify the request packet `[0x00, 0xB0, ...]` and response byte offsets for:
     - `Connection Status` (`0x03` = Online/Headset On, `0x02`/`0x00` = Headset Powered Off).
     - `Battery Capacity` (verify whether firmware returns stepped `0x00–0x04` or `0–100%`).
     - `Charging Flag` (`0x01` = Charging, `0x00` = Discharging).
3. **Hardware Control Opcodes:**
   - Extract byte structures for Sidetone (`0x39`), Auto-Shutoff Timer (`0xA3`), and Mic Mute LED brightness (`0x37`).

### Task 1.2: Extract Sonar's Auto-Categorization Database
Inspect `C:\Program Files\SteelSeries\GG\apps\sonar` and `C:\ProgramData\SteelSeries\GG` (SQLite databases and JSON bundles):
1. Locate Sonar's default **App Categorization Map** (the list of process names/executables automatically classified as `chat`—such as `Discord.exe`, `TeamSpeak.exe`, `Slack.exe`, `Zoom.exe`, `Telegram.exe`—versus `game` or `media`—such as `Spotify.exe`[cite: 3], browsers, and games).
2. Export this mapping into `Resources/default_app_categories.json` so our standalone app automatically routes known apps into `GAME` vs `CHAT` on first launch without user intervention.

---

## 3. Phase 2: Standalone Architecture (Zero GG Dependencies)

### Tech Stack
* **Framework:** C# / .NET 8 (`net8.0-windows10.0.19041.0`) with **WPF** + **WPF-UI (Fluent Design)** for a dark, modern, hardware-accelerated UI that opens from the system tray in `<20ms` and uses `<30MB RAM`.
* **Core Libraries:**
  * `HidSharp` — Non-blocking USB HID communication with the Nova 7 dongle (`VID 0x1038`).
  * `NAudio` (`NAudio.CoreAudioApi`) + custom COM interop for `IPolicyConfig` and `IAudioPolicyConfig` — Per-application audio session discovery, real-time peak meters, session volume control, and Windows default endpoint switching.
  * `CommunityToolkit.Mvvm` — Reactive UI state bindings.
  * `Microsoft.Toolkit.Uwp.Notifications` (or native `Windows.UI.Notifications`) — 30% battery toast alerts.

### How Standalone Game/Chat Mixing & App Routing Works (Without Sonar Virtual Drivers)
In the user's configuration, `Use the same device on all output channels` is enabled[cite: 2], meaning `GAME` and `CHAT` both output to the currently active primary playback device (e.g., `Headphones (Arctis Nova 7)` when powered on[cite: 3], or `L29w-30 (NVIDIA High Definition Audio)` when powered off[cite: 1, 2]).

Instead of installing unstable kernel virtual audio drivers, implement a **Real-Time WASAPI Session Group Mixer**:
1. **Session Discovery Loop:** Subscribe to `IAudioSessionManager2` (`OnSessionCreated`) on the active output endpoint and enumerate all active `AudioSessionControl` instances (`AudioSessionStateActive` or recently active).
2. **Logical Channel Assignment (`MASTER` / `GAME` / `CHAT` / `MIC`):**
   - Resolve each session's Process ID (`GetProcessID`) to its executable name, display name, and icon.
   - Check `config.json` for user-pinned overrides first; if none, check `default_app_categories.json`.
   - Known voice apps (`Discord.exe`, etc.) route to the `CHAT` column; games and media (`Spotify.exe`[cite: 3], etc.) route to the `GAME` column; unrecognized new apps appear in `Apps to be routed` under `MASTER` (or default to `GAME` while showing in the list)[cite: 1, 3].
   - Dragging an app pill (e.g., `[🎧 SPOTIFY]`[cite: 3]) between `GAME` and `CHAT` updates its logical channel in `config.json` and recalculates its volume multiplier immediately in `0ms` with zero audio drop.
3. **Mathematical Volume & Hardware ChatMix Calculation:**
   - Let:
     - $V_{\text{master}} \in [0.0, 1.0]$ = Master slider volume (controls physical endpoint `AudioEndpointVolume.MasterVolumeLevelScalar`)[cite: 1, 3].
     - $V_{\text{game}} \in [0.0, 1.0]$ = Game channel slider volume[cite: 1, 3].
     - $V_{\text{chat}} \in [0.0, 1.0]$ = Chat channel slider volume[cite: 1, 3].
     - $C \in [-1.0, +1.0]$ = ChatMix value (where $-1.0$ is 100% Game, $0.0$ is center detent, $+1.0$ is 100% Chat), driven by the **physical Nova 7 ChatMix wheel** over USB HID or the UI slider[cite: 1, 3].
   - Compute channel ChatMix scalars:
     - $M_{\text{game}} = \min(1.0, 1.0 - C)$
     - $M_{\text{chat}} = \min(1.0, 1.0 + C)$
   - For every app session assigned to `GAME`, set `SimpleAudioVolume.Volume`:
     $$V_{\text{session}} = V_{\text{game}} \times M_{\text{game}}$$
   - For every app session assigned to `CHAT`, set `SimpleAudioVolume.Volume`:
     $$V_{\text{session}} = V_{\text{chat}} \times M_{\text{chat}}$$
   - When the physical ChatMix wheel is turned on the headset (`~30-50ms` HID poll/interrupt), update $C$, animate the on-screen `CHATMIX` slider[cite: 1, 3], and update all active `GAME` and `CHAT` session volumes smoothly.
4. **Real-Time Peak Meters:**
   - Poll `AudioMeterInformation.MasterPeakValue` at `30 FPS` when the UI window is visible.
   - Render the cyan vertical peak bar inside each channel's slider track (`MASTER`, `GAME`, `CHAT`, `MIC`) matching the visual style in Sonar[cite: 3].

---

## 4. Phase 3: Auto-Switch (Backup Device Switch) Engine

Implement `DeviceSwitchService.cs` using the Windows undocumented `IPolicyConfig` COM interface (`{f8679f50-850a-41cf-9c72-430f290290c8}`) to switch the system Default Audio Playback (`eConsole`, `eMultimedia`) and Default Communications (`eCommunications`) endpoints instantaneously:

1. **Priority List State:**
   - Maintain an ordered list of preferred output devices in `config.json` (defaulting to: `1. Headphones (Arctis Nova 7)`, `2. L29w-30 (NVIDIA High Definition Audio)`, `3. Speakers (Realtek(R) Audio)`) and an `Excluded Devices` list[cite: 2].
2. **Headset Power-Off / Power-On Detection:**
   - The USB dongle for the Arctis Nova 7 stays plugged into the PC even when the headset is turned off, meaning Windows still sees the USB Audio endpoint as "Plugged In".
   - Use the USB HID status byte (`0xB0` -> `Connection Status`) to detect the **actual wireless power state** of the headset within `1-2 seconds`.
   - **When Nova 7 powers ON (`0x03`):** If `Headphones (Arctis Nova 7)` is higher in the priority list than the current device, automatically switch Windows Default Audio to `Headphones (Arctis Nova 7)` and re-bind the WASAPI Session Mixer to it[cite: 2, 3].
   - **When Nova 7 powers OFF (`!= 0x03`):** Immediately switch Windows Default Audio to the next available active device in the Priority List (e.g., `L29w-30 (NVIDIA High Definition Audio)`) and migrate the WASAPI Session Mixer volume scalars to that endpoint[cite: 1, 2].
3. **Boot/Reboot Sync:**
   - On application startup, evaluate the current Nova 7 wireless connection state and enforce the highest-priority active device[cite: 2].

---

## 5. Phase 4: Modern UI Specification

Build a clean, dark-themed UI (`#181A20` background, `#22252E` cards, `#00E5FF` / `#3B82F6` accents) with two main tabs plus a compact Header Bar—no login screens, ads, Moments, or bloatware[cite: 1, 2, 3]:

### Top Navigation & Status Header
* **Left:** Tab switcher: `[Mixer]` | `[Device Manager]` | `[Hardware & Backup]`[cite: 1, 2, 3].
* **Right:** Live Arctis Nova 7 status badge:
  * Connection dot (`🟢 Online` / `⚪ Offline`), Battery Percentage (`🔋 74%`), Charging bolt icon, and quick Sidetone dropdown (`Off / Low / Med / High`).

### View 1: `Mixer` Tab (Matches Sonar Mixer Layout)
* **4 Vertical Channel Columns (`MASTER`, `GAME`, `CHAT`, `MIC`):**
  * Header with icon, channel title, and active device sub-header showing device name + percentage (e.g., `🔗 Headphones (Arctis ... 100%` or `🔗 L29w-30 (NVIDIA Hi... 48%`)[cite: 1, 3].
  * Tall vertical volume fader with a live **cyan audio peak meter bar** rendered inside the lower track[cite: 3].
  * Mute toggle button (`🔊` / `🔇`) below each fader[cite: 1, 3].
  * **Apps Box (`Apps to be routed` / `Apps`):** Located beneath each channel strip[cite: 1, 3]. Displays currently active audio applications as rounded colored pills with the app's icon and name (e.g., `[🎧 SPOTIFY]`)[cite: 3]. Supports **Drag-and-Drop** between `MASTER`, `GAME`, and `CHAT`.
* **Bottom `CHATMIX` Bar:**
  * Spans across the bottom of the `GAME` and `CHAT` columns with `🎮` on the left and `💬` on the right[cite: 1, 3].
  * Moves automatically when the user turns the physical wheel on the Arctis Nova 7, and can also be dragged with the mouse[cite: 1, 3].

### View 2: `Device Manager` Tab (Matches Sonar Backup Device Switch Layout)
* **Two Side-by-Side Drag-and-Drop Lists:**
  * `OUTPUT DEVICE PRIORITY LIST`: Shows ordered cards (`1. Headphones (Arctis Nova 7)`, `2. L29w-30 (NVIDIA High Definition Audio)`, `3. Speakers (Realtek(R) Audio)`) with drag handles (`⠿`), a `🟢 CURRENT DEVICE` badge, and `NEXT / PREVIOUS DEVICE` indicators[cite: 2].
  * `EXCLUDED DEVICES`: Drop zone to ignore unwanted monitors or virtual devices[cite: 2].
* **Bottom Control Toggles:**
  * `Use the same device on all output channels` (Toggle)[cite: 2].
  * `BACKUP DEVICE SWITCH` (`Manual` / `Auto` segmented button)[cite: 2].
  * `Automatic backup device switch at boot/reboot` (Toggle)[cite: 2].
  * `Auto-switch when Nova Wireless headset is powered off` (Toggle)[cite: 2].
* **Right Panel (`SHORTCUTS`):**
  * Global hotkey recorder for `Previous Device`, `Next Device`, `Volume Up`, `Volume Down`, and `Mute`[cite: 2].

### View 3: `Hardware & Backup` Tab + System Tray Behavior
* **Battery Alert Threshold Slider:** Default set to **30%** with a `Test Notification` button. Fires a native Windows toast: *"Arctis Nova 7 at 30% — Plug in for 15 mins to avoid low-battery beeps."*
* **Hardware Controls:** Sidetone level (`0–3`), Inactive Shutoff Timer (`Never`, `5m`, `15m`, `30m`, `60m`), and Mic Mute LED Brightness (`Off`, `Low`, `Med`, `High`).
* **Backup & Restore:** `Export Settings (JSON)` and `Import Settings (JSON)` buttons, plus `Start Minimized to Tray with Windows` toggle.
* **System Tray Integration:** Left-clicking the tray icon toggles the window immediately; the tray icon dynamically renders the battery percentage and changes color at `<= 30%`.

---

## 6. Phase 5: Verification & Safe GG Uninstallation

1. **Build & Test:**
   - Run `dotnet build -c Release` and verify zero compiler warnings or errors.
   - Test HID reading: Confirm battery percentage, headset power state (`Online`/`Offline`), and physical ChatMix wheel turns are read accurately.
   - Test Audio Routing: Play audio in Spotify and a secondary app (e.g., browser or Discord). Drag `[🎧 SPOTIFY]`[cite: 3] between `GAME` and `CHAT` and turn the physical ChatMix wheel to verify real-time volume balancing.
   - Test Auto-Switch: Turn off the Arctis Nova 7 power button and verify Windows audio switches to `L29w-30 (NVIDIA High Definition Audio)` within 2 seconds[cite: 2]; turn the headset back on and verify audio returns to `Headphones (Arctis Nova 7)`[cite: 2, 3].
2. **Publish Standalone Executable:**
   - Compile a single self-contained binary:
     `dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true`
3. **Uninstall SteelSeries GG:**
   - Once all checks pass and `REA_FINDINGS.md` + `default_app_categories.json` are committed to the project repository, SteelSeries GG can be safely uninstalled from Windows.