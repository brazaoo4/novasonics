# NovaSonics Production Hardening & 24/7 Stability Plan

This document presents the findings of a comprehensive architectural, functional, and resource-leak audit of **NovaSonics**, followed by a concrete, file-by-file remediation roadmap organized into prioritized milestones (**P0**, **P1**, **P2**).

---

## Executive Summary of Audit Findings

### Part A: UI & Functional Verification (Live Testing Audit)

#### 1. Equalizer & Microphone "WASAPI Capture" / Software DSP Audit
* **Microphone Capture & Battery Drain (`WasapiSessionMixer.cs`, `MixerView.xaml:515`)**:
  * **Finding**: **No `WasapiCapture` stream exists anywhere in the codebase.** `WasapiSessionMixer` only queries `_defaultCaptureDevice.AudioMeterInformation.MasterPeakValue` and sets `AudioEndpointVolume.MasterVolumeLevelScalar` / `Mute`.
  * **Impact**: The headset microphone transmitter is **not** being forced open by NovaSonics, nor is it triggering the Windows microphone privacy indicator. However, `MixerView.xaml:515` displays the static badge `"ClearCast AI Noise Reduction Active via WASAPI Capture"`, which is factually false.
* **Equalizer & DSP Controls (`EqualizerView.xaml`, `EqualizerViewModel.cs`)**:
  * **10-Band Equalizer (`0x33`) — REAL HARDWARE, MISLEADING MULTI-CHANNEL UI**:
    * `ArctisNova7Device.ApplyEqBandGains()` sends a genuine USB HID command (`0x33` + `0x09` EEPROM commit) to the Nova 7 transceiver's onboard hardware DSP.
    * **Bug**: The Nova 7 hardware DAC only has **one global 10-band playback EQ**. However, `EqualizerView.xaml` presents 4 channel tabs (`Game`, `Media`, `Chat`, `Mic`). Selecting `Chat` or `Mic` and adjusting the EQ overwrites the headset's global playback EQ with voice/mic curves!
  * **Spatial Audio, Volume Boost & Smart Volume (`EqualizerView.xaml:284-510`) — 100% UI PLACEHOLDERS**:
    * `SpatialAudioEnabled`, `IsHeadphoneMode`, `SpatialTuning`, `SpatialDistance`, `VolumeBoostGain`, `SmartVolumeEnabled`, and `SmartVolumeLevel` only serialize to `config.json` (`SpatialProfiles`). Because NovaSonics operates without a kernel virtual audio driver (APO), there is no PCM audio pipeline intercepting per-channel audio streams.
  * **ClearCast AI, Noise Reduction, Noise Gate & Compressor (`EqualizerView.xaml:513-857`) — 100% UI PLACEHOLDERS**:
    * All voice processing controls under `Chat` and `Mic` only serialize to `config.json` (`VoiceProfiles`). They perform zero audio processing and send zero USB HID commands.

#### 2. EEPROM Flash Wear Protection (`0x09` Opcode) & Sidetone Binding
* **CRITICAL EEPROM Wear & HID Bus Lockup (`HardwareViewModel.cs:103-108`, `HardwareView.xaml:232`)**:
  * **Finding**: `HardwareMicVolume` is bound `TwoWay` to a WPF `<Slider Minimum="0" Maximum="7" Value="{Binding HardwareMicVolume, Mode=TwoWay}"/>` with default `UpdateSourceTrigger=PropertyChanged`.
  * **Impact**: Dragging the slider from `0` to `7` fires `OnHardwareMicVolumeChanged` up to 8 times in <200ms. Each call spawns a `Task.Run` calling `SetMicVolumeLevelAsync(value)` $\rightarrow$ `SendWriteAndSaveToFlash(0x37, val)`, which writes `0x37` AND immediately executes `CommitToFlash()` (`0x09` + `1530ms` `Thread.Sleep` inside `lock (_ioLock)`). A single slider drag queues **8 consecutive EEPROM flash writes** and stalls the USB HID bus for **over 12 seconds**!
  * **Same issue in `EqualizerViewModel.cs`**: Every click on a preset button (`SelectPreset`) or reset button (`ResetBands`) immediately triggers `ApplyToHeadsetAsync()` $\rightarrow$ `0x33` + `0x09` EEPROM flash write.
* **Sidetone Two-Way Binding & Sleep Timer Type Mismatch**:
  * Both the top header dropdown (`MainWindow.xaml:245`) and Settings dropdown (`HardwareView.xaml:155`) bind to the shared `HardwareViewModel.SidetoneLevel` property, so two-way synchronization works.
  * **Bug (`HardwareView.xaml:177`)**: The `Auto-Off Sleep Timer` ComboBox binds `SelectedValue="{Binding InactiveTimerMinutes, Mode=TwoWay}"` with `SelectedValuePath="Tag"`, where `Tag="15"` in XAML is a `System.String`, while `InactiveTimerMinutes` is an `int`. Due to strict WPF object equality (`"15".Equals(15) == false`), the ComboBox fails to display the current value on load!

#### 3. Non-Destructive ChatMix Multiplier & Inactive Session Desync
* **Multiplier Architecture (`WasapiSessionMixer.cs:592-625`)**:
  * `ApplyVolumes()` computes `effectiveGameVol = Math.Clamp(GameVolume * gameAtten, 0f, 1f)` and writes it to `s.SimpleAudioVolume.Volume` without mutating `GameVolume`, `MediaVolume`, or `ChatVolume`.
* **Critical Desync Bug (`WasapiSessionMixer.cs:612`)**:
  * Line 612 contains: `if (!s.IsSystemSoundsSession && s.State != AudioSessionState.AudioSessionStateActive) continue;`
  * **Impact**: If an application (e.g., Spotify or a paused game) is in `AudioSessionStateInactive` when the user adjusts `GameVolume`, `MediaVolume`, or `ChatMixBalance`, its WASAPI session volume is **skipped** and remains at its old level until the next 1000ms `RefreshSessions()` poll after audio resumes (causing a loud volume pop for up to 1 second when unpausing).

#### 4. App Pill Display Names, Stale Sessions & GDI Handle Churn
* **Raw Paths in Display Names (`WasapiSessionMixer.cs:381-403`)**:
  * `s.DisplayName` and `proc.MainWindowTitle` for console hosts (`cmd.exe`, `powershell.exe`) or background services often contain raw executable paths (`C:\WINDOWS\system32\cmd.exe`) or indirect Windows resource strings (`@%SystemRoot%\System32\AudioSrv.dll,-202`).
* **Per-Second Process & GDI Icon Extraction Churn (`WasapiSessionMixer.cs:397-420`)**:
  * **CRITICAL**: Every **1000ms** (`_sessionRefreshTimer`), `RefreshSessions()` calls `Process.GetProcessById(pid)`, inspects `proc.MainModule.FileName`, calls `Icon.ExtractAssociatedIcon(exePath)`, and creates a new `BitmapSource` via `Imaging.CreateBitmapSourceFromHIcon` for **every active audio session**!
  * Furthermore, `Icon.ExtractAssociatedIcon` allocates an unmanaged Win32 `HICON` handle that is **not destroyed** by `sysIcon.Dispose()` in all .NET GDI+ paths unless `DestroyIcon` is called or the icon is cached once per executable path.
* **Zombie / Exited Processes**:
  * Windows keeps `AudioSessionStateInactive` sessions in `AudioSessionManager.Sessions` long after a transient console app (`cmd.exe`) has exited or stopped playing audio. Currently, exited processes or long-inactive transient sessions remain stuck in the UI columns.

#### 5. Per-Channel Device Dropdown Routing (`AudioRoutingService.cs`)
* **Ambiguous Name Matching & Process Spawning**:
  * `AudioRoutingService.ResolveTargetDeviceArg()` truncates the device `FriendlyName` at `" ("` (e.g., `"Speakers (JBL Flip 6)"` $\rightarrow$ `"Speakers"`, `"Speakers (Realtek Audio)"` $\rightarrow$ `"Speakers"`). If the user has two `"Speakers"` or two `"Headphones"` devices, `SoundVolumeView.exe` routes audio to the wrong device!
  * Additionally, changing a channel dropdown spawns $N$ sequential `SoundVolumeView.exe` processes (`WaitForExit(1500)` each) inside a single task.

---

### Part B: 24/7 Daemon Stability & Resource Leaks

#### 1. COM & WASAPI Wrapper Churn
* **`WasapiSessionMixer.PeakTimer_Tick` (`WasapiSessionMixer.cs:709-793`)**:
  * Runs every **33ms (30 FPS)** and calls `var sessions = dev.AudioSessionManager.Sessions;`, instantiating new NAudio COM wrappers (`AudioSessionControl`, `AudioMeterInformation`, `SimpleAudioVolume`) 30 times per second per session, driving continuous Gen0/Finalizer GC pressure and COM RCWs.
* **`DeviceSwitchService.PurgeStalePropertyStoreKeys` (`DeviceSwitchService.cs:402-406`)**:
  * Enumerates `MMDeviceCollection` and accesses `ep.Properties` without wrapping `ep` in a `using` statement, leaking `IMMDevice` and `IPropertyStore` COM pointers.

#### 2. Zero-CPU Tray Idle Violations
* **Continuous 30 FPS Timers When Hidden to Tray**:
  * `MainViewModel._uiTimer` (`33ms` DispatcherTimer) and `WasapiSessionMixer._peakTimer` (`33ms` Timer) and `_sessionRefreshTimer` (`1000ms` Timer) run **24/7 even when `MainWindow` is hidden in the system tray (`Visibility != Visible`) or minimized**!
  * `MainWindow.UpdateTrayIconAndTooltip()` re-allocates a new `System.Drawing.Bitmap(16, 16)`, `Graphics`, `Font`, and Win32 `GetHicon()` **4 times per second (every 250ms)** on every `StatusUpdated` poll, even when battery percentage and connection state have not changed!
  * `ArctisNova7Device.PollingLoop()` polls USB HID every `250ms` even when the headset is powered off (`IsOnline == false`).

#### 3. Sleep/Wake & USB Disconnect Resilience
* **No `SystemEvents.PowerModeChanged` Handling**:
  * Neither `ArctisNova7Device` nor `DeviceSwitchService` listens for Windows Suspend/Resume events. Upon waking from S3/Modern Standby, USB controllers re-initialize asynchronously; immediate `PollingLoop` writes or `IMMNotificationClient` callbacks race against half-initialized endpoints.

#### 4. Endpoint Switching Race Conditions & Dispatcher Deadlocks
* **Un-debounced `IMMNotificationClient` Callbacks (`DeviceSwitchService.cs:270-346`)**:
  * Connecting a Bluetooth speaker or USB dongle triggers 5–12 rapid `OnDeviceStateChanged`, `OnDeviceAdded`, and `OnPropertyValueChanged` COM callbacks within 100ms. Each callback spawns an uncoordinated `Task.Run(() => RefreshDevices() + SwitchToDevice())`, causing concurrent COM enumeration and duplicate toast notifications.
* **Synchronous `Dispatcher.Invoke` Deadlock Risk (`MainViewModel.cs:96, 129, 138`, `HardwareViewModel.cs:54`)**:
  * Background HID threads call synchronous `Application.Current.Dispatcher.Invoke(...)` while holding or waiting on state, risking classic UI-thread/background-thread deadlocks during window close or device teardown.

#### 5. Non-Atomic Config I/O & Unbounded Crash Logging
* **`ConfigService.Save()` (`ConfigService.cs:45-60`)**:
  * Calls `File.WriteAllText(_configFilePath, json)` without a thread lock and without atomic `.tmp` write-and-rename. Concurrent saves from UI timers and background device events can throw `IOException` (file in use) or corrupt `config.json` to 0 bytes on sudden PC shutdown.
* **Crash Logging (`App.xaml.cs:29-42`)**:
  * Does not hook `TaskScheduler.UnobservedTaskException`, writes to `AppDomain.CurrentDomain.BaseDirectory` (which fails if installed in a protected folder), and has no file size cap.

---

## Prioritized Remediation Roadmap (Status: ALL COMPLETED & VERIFIED)

---

### Milestone P0: Critical Hardware Safety, Zero-CPU Tray Idle, Leaks & Deadlocks

#### [FIXED] P0.1 — Decouple Live HID Register Writes from `0x09` EEPROM Flash Commits
* **Files**:
  * `NovaSonics/Services/ArctisNova7Device.cs`
  * `NovaSonics/ViewModels/HardwareViewModel.cs`
  * `NovaSonics/Views/HardwareView.xaml`
  * `NovaSonics/ViewModels/EqualizerViewModel.cs`
* **Remediation**:
  1. In `ArctisNova7Device.cs`, split register writes from EEPROM persistence:
     * `SetSidetoneLevel`, `SetMicVolumeLevel`, `SetVolumeLimiter`, `SetInactiveTimer`, `SetMicMuteBrightness`, and `ApplyEqBandGains` accept `bool commitToFlash = false`.
     * When `commitToFlash == false`, send only the volatile register write (`0x39`, `0x37`, `0x3A`, `0xA3`, `0xAE`, `0x33`) with a 15ms inter-command pause—taking `<20ms` instead of `1530ms` and causing **zero** EEPROM wear.
     * Added a single debounced EEPROM commit timer (`_flashCommitDebounceTimer`, `2500ms` after the last register change) inside `ArctisNova7Device` that sends `0x09` once when the user finishes adjusting controls, plus an explicit `CommitToFlashAsync()` method for the `"Save to Headset"` button.
  2. In `HardwareView.xaml` & `HardwareViewModel.cs`, fixed the `Auto-Off Sleep Timer` ComboBox type mismatch by binding `ItemsSource="{Binding SleepTimerOptions}"` (`int[] { 0, 5, 15, 30, 60 }`) with `SelectedValuePath="Value"`.

#### [FIXED] P0.2 — Enforce True Zero-CPU When Minimized / Hidden to System Tray
* **Files**:
  * `NovaSonics/MainWindow.xaml.cs`
  * `NovaSonics/ViewModels/MainViewModel.cs`
  * `NovaSonics/Services/WasapiSessionMixer.cs`
  * `NovaSonics/Services/ArctisNova7Device.cs`
* **Remediation**:
  1. **Pause 30 FPS Meter & UI Timers on Hide/Minimize**:
     * Exposed `SetUiActive(bool isActive)` on `MainViewModel` and `SetActive(bool isActive)` on `WasapiSessionMixer`.
     * When `MainWindow` is hidden to tray (`Hide()`), minimized (`WindowState == WindowState.Minimized`), or when the active tab is **not** `Mixer` (`SelectedTabIndex != 0`), `MainViewModel._uiTimer` (33ms) and `WasapiSessionMixer._peakTimer` (33ms) are completely stopped.
     * When hidden to tray, `_sessionRefreshTimer` backs off from `1000ms` to `5000ms`.
  2. **Cache GDI Tray Icon (`MainWindow.xaml.cs`)**:
     * Tracks `_lastTrayBattery` (`int`), `_lastTrayOnline` (`bool`), and `_lastTrayCharging` (`bool`). Only regenerates the 16x16 GDI `Bitmap`/`HICON` in `RenderTrayIcon()` when state changes, and always calls `DestroyIcon` on the previous icon handle.
  3. **Adaptive HID Polling Interval (`ArctisNova7Device.cs`)**:
     * When the headset is powered on (`Status.IsHeadsetOnline`), polls `0xB0` every `1000ms` and polls `0x20`/`0xA0` once on connect and every `5000ms` thereafter.
     * When the headset is offline (`!Status.IsHeadsetOnline`), backs off polling to `2000ms`.

#### [FIXED] P0.3 — Eliminate Process/Icon GDI Handle Churn & COM Leaks
* **Files**:
  * `NovaSonics/Services/WasapiSessionMixer.cs`
  * `NovaSonics/Services/DeviceSwitchService.cs`
* **Remediation**:
  1. **Process Metadata & Icon Cache (`WasapiSessionMixer.cs`)**:
     * Created `ConcurrentDictionary<string, ProcessMetadata> _processMetadataCache` keyed by case-insensitive process name.
     * Extracts `FileVersionInfo` and `Icon.ExtractAssociatedIcon(exePath)` **once** per unique process name, freezes the `BitmapSource`, and explicitly calls Win32 `DestroyIcon(hIcon)` in a `finally` block.
  2. **Dispose `MMDevice` & COM Wrappers (`DeviceSwitchService.cs`, `WasapiSessionMixer.cs`)**:
     * Wrapped `MMDevice` enumerations in `using` blocks, released `PolicyConfigClient` RCWs via `Marshal.ReleaseComObject`, and enforced STA thread affinity on `WasapiSessionMixer.RebindEndpoint()` and `RefreshSessions()`.

#### [FIXED] P0.4 — Atomic Thread-Safe `config.json` Persistence & Rolling Crash Log
* **Files**:
  * `NovaSonics/Services/ConfigService.cs`
  * `NovaSonics/Models/NovaSonicsConfig.cs`
  * `NovaSonics/App.xaml.cs`
* **Remediation**:
  1. **Atomic Swap & Validation in `ConfigService`**:
     * Synchronized `Load()` and `Save()` with `_fileLock`, flushed `FileStream` (`fs.Flush(true)`), maintained `config.bak.json`, and used `File.Move(tmpPath, _configFilePath, overwrite: true)`.
     * Added `NovaSonicsConfig.ValidateAndSanitize()` on `Load`, `Save`, `Export`, and `Import`.
  2. **Global Fault Handling (`App.xaml.cs`)**:
     * Writes exceptions to `%APPDATA%\NovaSonics\crash.log` with a 1 MB size cap (rotates to `crash.old.log` when $>1\text{ MB}$).
     * Subscribed to `AppDomain.CurrentDomain.UnhandledException`, `DispatcherUnhandledException`, and `TaskScheduler.UnobservedTaskException`.
  3. **Replaced Synchronous `Dispatcher.Invoke` with `BeginInvoke`**:
     * Replaced all synchronous `Dispatcher.Invoke` calls across `MainViewModel`, `DeviceManagerViewModel`, `MixerViewModel`, `HardwareViewModel`, and `DeviceSwitchService` with non-blocking `Dispatcher.BeginInvoke`.

---

### Milestone P1: Core Mixer, Hardware Authenticity & Endpoint Routing

#### [FIXED] P1.1 — Remove Fake Software DSP Controls & Clarify Hardware EQ Scope
* **Files**:
  * `NovaSonics/Views/MixerView.xaml`
  * `NovaSonics/Views/EqualizerView.xaml`
  * `NovaSonics/ViewModels/EqualizerViewModel.cs`
  * `NovaSonics/Models/NovaSonicsConfig.cs`
* **Remediation**:
  1. **Authentic Hardware Mic Status (`MixerView.xaml`)**:
     * Replaced the static WASAPI Capture badge with live Hardware Mic Gain and Sidetone telemetry bound directly to `HardwareViewModel`.
  2. **Unified 10-Band Hardware DAC EQ (`EqualizerView.xaml`, `EqualizerViewModel.cs`, `NovaSonicsConfig.cs`)**:
     * Removed non-functional software DSP placeholders (`SpatialProfiles`, `VoiceProfiles`, `SpatialAudio`, `SmartVolume`, `ClearCast AI`, `NoiseGate`, `Compressor`) and scoped the 10-band Hardware DAC Equalizer around genuine hardware profile slots (`Game`, `Media`, `Voice`, `Custom`).

#### [FIXED] P1.2 — Non-Destructive ChatMix & Instant Volume Enforcement on Inactive Sessions
* **Files**:
  * `NovaSonics/Services/WasapiSessionMixer.cs`
* **Remediation**:
  1. In `WasapiSessionMixer.ApplyVolumes()`, scalar volumes are applied immediately to both `Active` and `Inactive` (paused) sessions across all processes matching the app name.
  2. `SessionEventClient.OnStateChanged` immediately re-applies `ApplyVolumes()` when a session transitions to `AudioSessionStateActive`.

#### [FIXED] P1.3 — Fix Per-Channel Device Routing & Debounce Endpoint Switching
* **Files**:
  * `NovaSonics/Services/AudioRoutingService.cs`
  * `NovaSonics/Services/DeviceSwitchService.cs`
* **Remediation**:
  1. **Unambiguous Device Routing (`AudioRoutingService.cs`)**:
     * Passes full device identifiers/names to `SoundVolumeView.exe /SetAppDefault` with `File.Exists` verification, `CreateNoWindow = true`, `UseShellExecute = false`, and `WaitForExit(1500)` on a background thread.
  2. **300ms Topology Change Debounce & Self-Switch Suppression (`DeviceSwitchService.cs`)**:
     * Coalesces rapid `IMMNotificationClient` events into a single `300ms` debounce timer (`_topologyDebounceTimer`) and suppresses self-triggered `OnDefaultDeviceChanged` recursion via `_isSwitchingEndpoint` and `_switchCooldownExpiry`.

---

### Milestone P2: UX Polish, Clean App Pill Names & Sleep/Wake Resilience

#### [FIXED] P2.1 — Clean App Pill Display Names & Prune Stale/Exited Sessions
* **Files**:
  * `NovaSonics/Services/WasapiSessionMixer.cs`
* **Remediation**:
  1. **Display Name Resolution Chain**:
     * Resolves clean friendly names via `_friendlyProcessNames`, `FileVersionInfo.GetVersionInfo(exePath).FileDescription`, sanitized `MainWindowTitle`, or title-cased `procName`, rejecting raw paths and `@%SystemRoot%` resource strings.
  2. **Prune Exited & Stale Sessions**:
     * Checks `proc.HasExited` (handling `Win32Exception` Access Denied on elevated games safely) and prunes inactive unpinned sessions (>30s) and transient console sessions (`cmd`, `powershell`, `pwsh`, `conhost`, `wt`, `OpenConsole`).

#### [FIXED] P2.2 — Windows Sleep/Wake & USB Re-Enumeration Resilience
* **Files**:
  * `NovaSonics/Services/ArctisNova7Device.cs`
  * `NovaSonics/Services/DeviceSwitchService.cs`
* **Remediation**:
  1. Subscribed to `Microsoft.Win32.SystemEvents.PowerModeChanged` in both `ArctisNova7Device` and `DeviceSwitchService`, pausing HID polling and endpoint switching on `PowerModes.Suspend` and waiting `2500ms` on `PowerModes.Resume` before re-initializing streams and endpoints.

---

## Verification Matrix

| Milestone | Status | Verification Procedure | Pass Criteria |
| :--- | :---: | :--- | :--- |
| **P0.1 (EEPROM Safety)** | **[FIXED]** | Drag `Hardware Mic Volume` slider rapidly back and forth (`0` $\leftrightarrow$ `7`) in Settings. | Mic gain changes in real time on the headset with zero UI lag; `0x09` flash commit executes only once 2.5s after release. Sleep timer ComboBox displays current value (`15 Minutes`). |
| **P0.2 (Zero-CPU Tray)** | **[FIXED]** | Close main window to system tray and monitor `NovaSonics.exe` in Task Manager / Process Explorer. | `0.0%` CPU usage in tray idle; zero GDI object count growth (`RenderTrayIcon` caches battery/online/charging state). |
| **P0.3 (Icon & COM Leaks)** | **[FIXED]** | Open Mixer tab with active audio apps and watch Process Explorer `Handles` and `GDI Objects`. | Zero handle or GDI object leaks; `ProcessMetadata` and `ExtractAssociatedIcon` run once per unique process name with `DestroyIcon`. |
| **P0.4 (Atomic Config)** | **[FIXED]** | Rapidly drag sliders across tabs while inspecting `%APPDATA%\NovaSonics\`. | `config.json` updates atomically via `.tmp` swap and `config.bak.json` backup with zero `IOException` crashes. |
| **P1.1 (Authentic UI)** | **[FIXED]** | Inspect Mixer `MIC` column and `Equalizer` view. | Fake WASAPI Capture text and non-functional software DSP placeholders are gone; 10-band hardware DAC EQ curves apply cleanly to the headset. |
| **P1.2 (ChatMix)** | **[FIXED]** | Pause Spotify (`Media`), move `ChatMix` slider toward `Chat`, then unpause Spotify. | Spotify resumes at the attenuated volume immediately without a 1-second loud volume pop; base `Game`/`Media` faders remain untouched. |
| **P1.3 (Channel Routing)** | **[FIXED]** | Uncheck `Sync all channels with default device` and assign `Media` to a secondary speaker. | Active Media apps route to the exact selected endpoint within `<500ms`. |
| **P2.1 (App Pills)** | **[FIXED]** | Trigger a sound in `cmd.exe`, then close `cmd.exe`. | Pill displays `"Command Prompt"` instead of `C:\WINDOWS\system32\cmd.exe` and disappears within 1s of closing `cmd.exe`. |
| **P2.2 (Sleep/Wake)** | **[FIXED]** | Put Windows to Sleep and wake it with the Nova 7 powered on. | App reconnects to HID dongle and restores priority audio endpoint cleanly with zero unhandled exceptions. |
