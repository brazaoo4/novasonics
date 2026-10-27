# NovaSonics — Emergency Crash Root-Cause Analysis & Adversarial QA Bug Audit

This document records all root causes discovered during the Phase 0A emergency crash investigation and the Phase 0B adversarial QA / static code audit, along with their verification status across Phases 1–5.

---

## Part 1: Root Causes of Emergency Crashes (Phase 0A)

### 1. Startup Hang / Crash & Background Daemon Lockup (`App.xaml.cs`, `MainViewModel.cs`, `WasapiSessionMixer.cs`) — `[FIXED]`
* **Symptom**: Launching `NovaSonics` either hung indefinitely on startup, exited silently without showing a window, or crashed with `System.InvalidCastException: Specified cast is not valid at NAudio.CoreAudioApi.Interfaces.IMMDevice.GetId(String& id)`.
* **Root Causes**:
  1. **Premature Timer & HID Thread Startup (`App.xaml.cs`, `MainViewModel.cs`)**: `_mixerService.Initialize()` (which started the 33ms `_peakTimer` and 1000ms `_sessionRefreshTimer`) and `_headsetService.Start()` (which performed synchronous blocking USB HID `0xB0`/`0x20`/`0xA0` I/O and started background polling threads) were invoked **before** `new MainWindow(...)` and before the UI visual tree was initialized.
  2. **Synchronous `Dispatcher.Invoke` Deadlocks (`DeviceManagerViewModel.cs`, `MixerViewModel.cs`)**: Background threads (`PowerStateChanged`, `DefaultDeviceChanged`, `DevicesRefreshed`) invoked synchronous `Dispatcher.Invoke(...)` while the main UI thread was inside constructor initialization or COM calls, risking classic STA/MTA deadlocks.
  3. **Cross-Thread STA/MTA COM Marshaling Crash (`WasapiSessionMixer.cs:339`, `crash.log`)**: `WasapiSessionMixer` created its `MMDeviceEnumerator` on the STA UI thread during construction, then accessed `MMDevice` RCW instances (`_defaultRenderDevice`, `_cachedRenderDevices`) across both MTA COM callback threads (`OnDefaultDeviceChanged`) and the STA `DispatcherTimer` thread without thread-local enumerator isolation, causing `InvalidCastException` (`E_NOINTERFACE` / `RPC_E_WRONG_THREAD`) in `StubHelpers.GetCOMIPFromRCW`.
  4. **Orphaned Background Process Blocking Re-Launches (`App.xaml.cs`, `MainWindow.xaml.cs`)**: When the previous instance crashed or hid to tray before `SourceInitialized` registered `RegisterWindowMessage("NovaSonics_ShowMainWindow")`, `PostMessage(HWND_BROADCAST, wmShow, ...)` failed to wake the existing process, causing every subsequent launch attempt to exit at `Environment.Exit(0)` with no visible window.
* **Fix**:
  * Deferred `_mixerService.Initialize()` and `mainVm.StartServices()` until **after** `MainWindow` is constructed and shown.
  * Moved initial `_headsetService.Start()` and `EnforcePriorityDevice()` onto a non-blocking background task.
  * Added a named `EventWaitHandle` (`Global\NovaSonics_ShowWindow_Event`) listener thread alongside `Global\NovaSonics_SingleInstance_Mutex` so launching a second instance reliably wakes and focuses the main window even before `HwndHook` is active.
  * Replaced all synchronous `Dispatcher.Invoke` calls with asynchronous `Dispatcher.BeginInvoke`.

### 2. "Set Default Device" Immediate Crash (`IPolicyConfig.cs`, `DeviceSwitchService.cs`, `DeviceManagerViewModel.cs`) — `[FIXED]`
* **Symptom**: Clicking `Set Default` on an audio device in the `Devices` tab or switching devices caused an immediate crash or re-entrant freeze.
* **Root Causes**:
  1. **Unmarshaled String Parameters in `IPolicyConfig` Vtable (`IPolicyConfig.cs`)**: Preceding vtable methods (`GetMixFormat`, `GetDeviceFormat`, `ResetDeviceFormat`, etc.) omitted `[In, MarshalAs(UnmanagedType.LPWStr)]` on `pszDeviceName`, and no fallback existed for `IPolicyConfigVista` (`{568b9108-44bf-40b4-9006-86afe5b5a620}`), nor was `Marshal.ReleaseComObject` called on `PolicyConfigClient`.
  2. **Re-Entrant `IMMNotificationClient.OnDefaultDeviceChanged` Feedback Loop (`DeviceSwitchService.cs`)**: Calling `policyConfig.SetDefaultEndpoint` for `eConsole`, `eMultimedia`, and `eCommunications` immediately fires 3 synchronous/MTA `OnDefaultDeviceChanged` callbacks from Windows AudioSrv—often before `SetDefaultEndpointForAllRoles` even returns. `OnDefaultDeviceChanged` cleared `_pendingDefaultDeviceId` on the first role (`eConsole`) and then processed `eMultimedia` as an external change, firing `DefaultDeviceChanged` -> `MixerViewModel` -> `RebindEndpoint` concurrently with `SwitchToDevice`!
  3. **Cross-Thread `ObservableCollection` Mutation in `SwitchCaptureDevice` (`DeviceSwitchService.cs:461`)**: `SwitchCaptureDevice` iterated and mutated `CaptureDevices` directly on a background thread (`Task.Run` from `MixerViewModel.OnSelectedMicDeviceChanged`) without `RunOnUI`.
* **Fix**:
  * Verified all 12 methods in `IPolicyConfig` in exact Windows 10/11 vtable order with explicit `[PreserveSig]` and `[In, MarshalAs(UnmanagedType.LPWStr)]`, added `IPolicyConfigVista` fallback, and released the COM object in a `finally` block.
  * Added `_isSwitchingEndpoint` volatile guard flag and `_switchCooldownExpiry` timestamp in `DeviceSwitchService` to suppress self-triggered `OnDefaultDeviceChanged` callbacks across all 3 role switches.
  * Wrapped `CaptureDevices` mutations in `SwitchCaptureDevice` inside `RunOnUI(...)`.

---

## Part 2: Adversarial QA & Deep Static Bug Hunt Findings (Phase 0B)

### Category 1: Destructive User Input & UI Chaos (`Views/` & `ViewModels/`)
1. **BUG-QA-01: Import Config Fuzzing & Out-of-Bounds Values (`ConfigService.cs`, `NovaSonicsConfig.cs`)** — `[FIXED]`
   * **Finding**: Importing a JSON file with `MasterVolume: 99.0`, `ChatMixBalance: -50.0`, `NaN`/`Infinity` floats, `SidetoneLevel: 99`, `EqualizerBands` of length != 10, or `null` collections (`PriorityDevices: null`, `AppChannelAssignments: null`) left invalid state in `Config`, crashing sliders (`IndexOutOfRangeException` on `Bands[i]`) or sending invalid bytes over USB HID.
   * **Fix**: Implemented `NovaSonicsConfig.ValidateAndSanitize()` and invoked it on every `Load()`, `Save()`, `ExportConfig()`, and `ImportConfig()`.
2. **BUG-QA-02: Drag-and-Drop Stale Session / Same-Column Abuse (`MixerView.xaml.cs`, `MixerViewModel.cs`)** — `[FIXED]`
   * **Finding**: If an application closes while its pill is mid-drag, or if the user drops a pill onto the column it already belongs to, `MoveAppToChannel` still re-saved config, spawned `SoundVolumeView.exe`, and re-assigned volumes for a dead session. Furthermore, `DragDrop.DoDragDrop` can throw `COMException` if the clipboard/OLE drag state is busy.
   * **Fix**: Wrapped `DragDrop.DoDragDrop` in `try/catch` in both `MixerView.xaml.cs` and `DeviceManagerView.xaml.cs`. In `MixerViewModel.MoveAppToChannel`, verify the session is still present in `AllSessions` and `app.Channel != targetChannel` before executing routing.
3. **BUG-QA-03: Priority List Empty / All Devices Offline Edge Case (`DeviceManagerViewModel.cs`, `DeviceSwitchService.cs`)** — `[FIXED]`
   * **Finding**: Excluding every playback device or having all devices unplugged caused `SwitchNextDevice()` / `SwitchPreviousDevice()` or `EnforcePriorityDevice()` to operate on empty candidate lists.
   * **Fix**: Guarded all index arithmetic with `candidates.Count == 0` checks and safe fallback when `CurrentDefaultDevice` is `null`.
4. **BUG-QA-04: Two-Way Binding Feedback Loop on `ChatMixChanged` (`MainViewModel.cs`, `MixerViewModel.cs`)** — `[FIXED]`
   * **Finding**: Every 1000ms when `ArctisNova7Device` polled `0xB0`, if `ChatMixChanged` fired, `MainViewModel` set `MixerVM.ChatMixBalance = balance`, triggering `OnChatMixBalanceChanged` -> `QueueSave()` even if the balance hadn't changed.
   * **Fix**: Added `MixerViewModel.SetChatMixFromHardware(float balance)` with a `Math.Abs(ChatMixBalance - clamped) < 0.001f` deadband and `_isExternalUpdate` guard.

### Category 2: OS & Audio Subsystem Chaos (`WasapiSessionMixer.cs`, `DeviceSwitchService.cs`, `AudioRoutingService.cs`)
5. **BUG-QA-05: Cross-Thread COM `InvalidCastException` in `WasapiSessionMixer` (`WasapiSessionMixer.cs`)** — `[FIXED]`
   * **Finding**: `IMMNotificationClient.OnDefaultDeviceChanged` runs on a Windows MTA thread pool thread and called `RebindEndpoint(defaultDeviceId)` directly, which instantiated `MMDevice` COM objects on the MTA thread and stored them in `_defaultRenderDevice` / `_cachedRenderDevices`. Subsequent access on the STA `DispatcherTimer` thread threw `System.InvalidCastException: Specified cast is not valid` inside `IMMDevice.GetId`.
   * **Fix**: Marshal `WasapiSessionMixer.OnDefaultDeviceChanged` and `RebindEndpoint` to the Dispatcher thread (or synchronize COM enumeration cleanly) and catch `COMException` (`0x88890004` `AUDCLNT_E_DEVICE_INVALIDATED`, `0x80070490` `E_NOTFOUND`), `InvalidComObjectException`, and `InvalidCastException` on all COM calls.
6. **BUG-QA-06: "Zero Audio Devices" State (`WasapiSessionMixer.cs`, `DeviceSwitchService.cs`)** — `[FIXED]`
   * **Finding**: When GPU drivers update and no render/capture endpoints exist, `GetDefaultAudioEndpoint` throws `COMException` (`0x80070490` Element not found).
   * **Fix**: Safely catch `COMException` when querying default render and capture endpoints, set `_defaultRenderDevice = null` / `_defaultCaptureDevice = null`, and zero peak meters without crashing timers.
7. **BUG-QA-07: Access Denied on Elevated / Anti-Cheat Processes (`WasapiSessionMixer.cs`)** — `[FIXED]`
   * **Finding**: Inspecting `proc.MainModule` or `proc.HasExited` on an elevated Administrator game or Anti-Cheat protected process throws `Win32Exception` (Access Denied) on non-admin instances. Previously, `if (proc.HasExited)` inside `try/catch` caught `Win32Exception` and executed `continue`, **hiding elevated games from the mixer**!
   * **Fix**: Separated `Process.GetProcessById(pid)` from the `proc.HasExited` check: if `proc.HasExited` throws `Win32Exception` (Access Denied), treat the process as alive (`hasExited = false`) and fall back to `proc.ProcessName` so elevated games always appear in the mixer!

### Category 3: Hardware & USB Bus Chaos (`ArctisNova7Device.cs`)
8. **BUG-QA-08: Periodic `0x20`/`0xA0` Hardware Register Sync (`ArctisNova7Device.cs`)** — `[FIXED]`
   * **Finding**: `ReadHardwareSettingsFromDevice()` (`0x20` and `0xA0`) was only called once on initial connect, rather than once on connect and every `5000ms` thereafter as required by the spec.
   * **Fix**: Added a timestamp tracker (`_lastSettingsPollTime`) in `PollingLoop` so `0x20`/`0xA0` are polled on connect and every `5000ms` while online.
9. **BUG-QA-09: Uninitialized Telemetry & Dongle Yank Handling (`ArctisNova7Device.cs`, `MainViewModel.cs`)** — `[FIXED]`
   * **Finding**: When the USB dongle is yanked mid-write/read, `HidStream` throws `IOException` or `ObjectDisposedException`, which must immediately transition `Status.IsDeviceConnected = false` and `Status.IsHeadsetOnline = false`, dispose streams, and notify `PowerStateChanged(false)`.
   * **Fix**: Updated `PollingLoop`, `SendAndReceiveCommand`, and `SendWriteRegister` to catch `IOException`/`ObjectDisposedException`, close streams, and transition `Status.IsHeadsetOnline = false`.

### Category 4: Lifecycle & Shutdown Chaos (`MainWindow.xaml.cs`, `App.xaml.cs`)
10. **BUG-QA-10: Windows Logoff Blocking (`MainWindow.xaml.cs`, `App.xaml.cs`)** — `[FIXED]`
    * **Finding**: `MainWindow.OnClosing` unconditionally set `e.Cancel = true` when `!_isExplicitExit && _configService.Config.StartMinimizedToTray`, blocking Windows Logoff/Restart if the window was open.
    * **Fix**: Subscribed to `OnSessionEnding` in `App.xaml.cs` (`IsSessionEnding = true`) and checked `!isSessionEnding` in `MainWindow.OnClosing`.

---

## Part 3: Post-Normalization Security, Concurrency & Native Resource Audit (v1.0.0)

11. **BUG-QA-11: Cross-Thread `MMDeviceEnumerator` Access in `AudioRoutingService` (`AudioRoutingService.cs`)** — `[FIXED]`
    * **Finding**: `RouteAppToDevice` and `RouteAppsToDevice` invoked `ResolveTargetDeviceId` on the caller thread before queuing `Task.Run`, while other callers invoked `RouteAppsToDevice` inside `Task.Run`, causing concurrent unsynchronized `_enumerator.EnumerateAudioEndPoints(...)` COM calls across STA and MTA threads.
    * **Fix**: Added `_enumeratorLock` and `_disposed` guards around `_enumerator.EnumerateAudioEndPoints(...)` and moved `ResolveTargetDeviceId` inside the background `Task.Run` block.
12. **BUG-QA-12: Stale Per-Process Routing Cache Across App Restarts & Sync Mode (`WasapiSessionMixer.cs`)** — `[FIXED]`
    * **Finding**: `_lastRoutedDeviceByProcess` was keyed only by `procName` and never invalidated when an application exited and restarted with a new PID or when `ClearAllPersistedEndpoints()` was called, preventing restarted apps from being re-routed. Additionally, `SetAppChannel` routed apps even when `UseSameDeviceOnAllChannels` was `true`.
    * **Fix**: Keyed `_lastRoutedDeviceByProcess` by `$"{pid}:{procName}"`, pruned exited PIDs on every `RefreshSessions()` pass, cleared the cache on `RebindEndpoint()`, and respected `UseSameDeviceOnAllChannels` inside `SetAppChannel`.
13. **BUG-QA-13: Cross-Thread `ObservableCollection` Iteration in `MixerViewModel` (`MixerViewModel.cs`)** — `[FIXED]`
    * **Finding**: `OnSelectedMasterDeviceChanged` iterated `AllSessions` (`ObservableCollection<AppSessionModel>` owned by the UI Dispatcher) inside a background `Task.Run` block, risking `InvalidOperationException` if `RefreshSessions()` modified `ActiveSessions` concurrently.
    * **Fix**: Replaced manual `AllSessions` iteration and `RouteAppsToDevice(allApps, "DefaultRenderDevice")` with `_routingService.ClearAllPersistedEndpoints()`, which atomically clears all persisted endpoints via `IAudioPolicyConfigFactory::ClearAllPersistedApplicationEndpoints` (vtable Slot 27).
14. **BUG-QA-14: Live ViewModel Refresh After Settings Import & Tab Switching (`ConfigService.cs`, `MainViewModel.cs`, `MixerViewModel.cs`, `DeviceManagerViewModel.cs`, `EqualizerViewModel.cs`)** — `[FIXED]`
    * **Finding**: Importing a settings backup in `HardwareViewModel` updated `_configService.Config` on disk and in `HardwareViewModel`, but `MixerViewModel`, `DeviceManagerViewModel`, and `EqualizerViewModel` retained their previous in-memory values until app restart. Furthermore, toggling `"Sync all channels with default device"` in the `Devices` tab and switching back to `Mixer` did not rebind the per-channel device selectors because `MixerView` remains in the visual tree (`Visibility.Collapsed`).
    * **Fix**: Added `ConfigImported` event to `IConfigService` and `ReloadFromConfig()` methods to `MixerViewModel`, `DeviceManagerViewModel`, and `EqualizerViewModel`, plus called `MixerVM.RebindSelectedDevices()` on `SelectTab(0)`.
15. **BUG-QA-15: Native `Process` Handle & GDI+ `StringFormat` Leak in Periodic Tasks (`MainWindow.xaml.cs`)** — `[FIXED]`
    * **Finding**: `TrimMemory()` (executed every 30 seconds) called `Process.GetCurrentProcess().Handle` without disposing the returned `Process` wrapper (`SafeProcessHandle`), and `RenderTrayIcon()` created a `System.Drawing.StringFormat` (wrapping a native GDI+ `GpStringFormat` handle) without `using`.
    * **Fix**: Wrapped both `using var proc = Process.GetCurrentProcess();` and `using var sf = new StringFormat { ... };` in `using` statements.
16. **BUG-QA-16: Unbounded JSON Config Import & Single-Instance Session Scope (`ConfigService.cs`, `App.xaml.cs`)** — `[FIXED]`
    * **Finding**: `ConfigService.ImportConfig` called `File.ReadAllText(filePath)` without checking file length (allowing multi-gigabyte files to trigger OOM), and `App.xaml.cs` used `Global\` namespace for single-instance mutex/event IPC (which can collide across Windows Fast User Switching / RDP sessions). Also `_routingService` was not disposed in `App.OnExit`.
    * **Fix**: Enforced a 2 MB maximum file size check in `ImportConfig`, scoped single-instance IPC primitives to `Local\`, and disposed `_routingService` in `App.OnExit`.
17. **BUG-QA-17: Exception-Safe `MMDevice` Grouping in `DeviceSwitchService` (`DeviceSwitchService.cs`)** — `[FIXED]`
    * **Finding**: `RefreshDevices()` called `endpointList.GroupBy(e => e.FriendlyName)` outside the per-device `try/finally` disposal loop, risking leaked `MMDevice` COM wrappers if a USB audio device disconnected between initial list population and `GroupBy` evaluation.
    * **Fix**: Cached `(MMDevice Device, string FriendlyName)` tuples during the initial `try/catch` pass so `GroupBy` operates purely on managed strings.
18. **BUG-QA-18: UI Dispatcher Hang & AudioSrv Deadlock on Headset Power-Down (`WasapiSessionMixer.cs`, `DeviceSwitchService.cs`, `MixerViewModel.cs`, `MainViewModel.cs`)** — `[FIXED]`
    * **Finding**: Turning off the wireless headset triggered a cascade that froze the UI thread (`AppHang_NovaSonics.exe`): (1) `WasapiSessionMixer.RebindEndpoint()` forced itself onto the UI Dispatcher and called `IAudioEndpointVolume::UnregisterControlChangeNotify` and `Dispose()` synchronously, deadlocking against Windows `AudioSrv` locks; (2) `MixerViewModel` subscribed to `CollectionChanged` across three device collections, flooding the Dispatcher with dozens of `RebindSelectedDevices` operations during device refresh; (3) `OnSelected<Channel>DeviceChanged` lacked equality guards, re-triggering endpoint switching in an infinite loop; (4) `RefreshDevices()` had an unwanted side-effect calling `SetDefaultEndpointForAllRoles` during queries; (5) `_peakTimer` ran COM queries directly on the UI Dispatcher thread.
    * **Fix**: Migrated `_peakTimer` and `_sessionRefreshTimer` from `DispatcherTimer` to MTA `System.Threading.Timer` instances; offloaded COM device unregistration/disposal to detached background tasks outside of `_sessionLock`; removed `CollectionChanged` subscriptions from `MixerViewModel` and added equality guards on all channel device selectors; removed hardware switching side-effects from `RefreshDevices()`; and wrapped `HandleHeadsetPowerStateChanged` in `Task.Run`.

---

## Part 4: Arctis Nova 7 Microphone & Hardware PEQ Audit, Device Switching & Performance Hardening (v1.1.0)

19. **BUG-QA-19: Headset Microphone Muting & EEPROM DSP Corruption on PID 0x22A1 (`ArctisNova7Device.cs`, `MainViewModel.cs`, `EqualizerViewModel.cs`, `NovaSonicsConfig.cs`)** — `[FIXED]`
    * **Finding**: The user's microphone stopped working and required a factory reset in SteelSeries GG. Investigation revealed two compounding root causes:
      1. Live `%APPDATA%\NovaSonics\config.json` contained `"HardwareMicVolume": 0`. In `ArctisNova7Device.ReadHardwareSettingsFromDevice()`, polling `0x20` every 5 seconds read an uninitialized/waking zero response (`[0x00, 0x20, 0x00, 0x00, 0x00]`) or stale HID input buffer due to lack of response draining, which set `Status.MicVolumeLevel = 0` and persisted `HardwareMicVolume = 0` (hardware preamp gain 0 / muted).
      2. In commit `aaae153`, `SetEqualizerBandsAsync` was changed to unconditionally send the Gen 1 10-byte Graphic EQ packet (`0x33`) to `0x22A1` (Upgrade / Gen 2 firmware). On `0x22A1`, opcode `0x33` is Parametric EQ (`parametric_eq`) expecting `connection_type` at byte 2 (`0x00` = 2.4G, `0x01` = BT, `0x02` = Microphone PEQ) followed by ten 6-byte biquad filter structs. The 10-byte packet set `connection_type = b0` (where `b0 == 2` corrupts Microphone PEQ) and filled biquads with invalid `frequency = 0` and `q_factor = 0`, burned to EEPROM via `0x09` on every single power-on in `MainViewModel.OnPowerStateChanged`.
    * **Fix**:
      - Restored the 4-packet Gen 2 PEQ sequence (`0xA7` + `0x33` with `connection_type = 0x00` and valid biquad bounds + `0x27` + `0x09`) for all Gen 2/Upgrade PIDs (`0x22A1`, `0x227E`, `0x229E`, etc.), reserving 10-byte Graphic EQ strictly for `LegacyBatteryScalePids`.
      - Added `ResetMicrophoneDspAndGainAsync()` helper and wired it to `EqualizerViewModel.ResetFlat` to allow one-click reset/repair of headset microphone PEQ and gain (`0x37 = 7`).
      - In `SendAndReceiveCommand()`, drained stale unread input reports from `_commandStream` before writing and verified opcode echo.
      - In `ReadHardwareSettingsFromDevice()`, guarded against all-zero payloads and stopped running `0x20` queries every 5 seconds (reading once on connect after settle delay).
      - Auto-healed `HardwareMicVolume <= 0` to `7` in `NovaSonicsConfig.ValidateAndSanitize()`.
      - Removed redundant `AutoApplyToHeadset()` burn from `MainViewModel.OnPowerStateChanged`.

20. **BUG-QA-20: Repeated `InvalidCastException` Crashes in `TrySyncCaptureDevice` (`DeviceSwitchService.cs`, `crash.log`)** — `[FIXED]`
    * **Finding**: `%APPDATA%\NovaSonics\crash.log` logged dozens of `System.InvalidCastException: Unable to cast COM object of type 'System.__ComObject' to interface type 'NAudio.CoreAudioApi.Interfaces.IMMDevice'... E_NOINTERFACE at DeviceSwitchService.<>c__DisplayClass45_2.<TrySyncCaptureDevice>b__3()`. `RunOnUI` used `Dispatcher.BeginInvoke` (asynchronous), while `finally { cap.Dispose(); }` ran on the calling background thread before the UI thread accessed `cap.ID`.
    * **Fix**: Extracted `string capId = cap.ID;` and `string capName = cap.FriendlyName;` into local variables before calling `SetDefaultEndpointForAllRoles` and before `cap.Dispose()`, passing only the immutable `capId` string into `RunOnUI`.

21. **BUG-QA-21: Input (Capture) Device Default Changes & Hotplugs Ignored (`DeviceSwitchService.cs`, `WasapiSessionMixer.cs`)** — `[FIXED]`
    * **Finding**: `IMMNotificationClient.OnDefaultDeviceChanged` in both `DeviceSwitchService` and `WasapiSessionMixer` checked only `flow == DataFlow.Render`, ignoring `DataFlow.Capture` completely. In addition, `OnDeviceStateChanged`, `OnDeviceAdded`, and `OnDeviceRemoved` were dropped when `_isSwitchingEndpoint` was active, and `WasapiSessionMixer.RefreshSessions()` only checked if the default Render endpoint changed.
    * **Fix**: Added `DataFlow.Capture` handling to `OnDefaultDeviceChanged` in both services; checked default capture endpoint ID in `WasapiSessionMixer.RefreshSessions()`; removed `if (_isSwitchingEndpoint) return;` from device state/add/remove topology events; and queried `Role.Multimedia` first (with fallbacks to `Role.Console` and `Role.Communications`) for capture endpoints.

22. **BUG-QA-22: Smart Auto-Switch Device Hijacking (`DeviceSwitchService.cs`)** — `[FIXED]`
    * **Finding**: In `OnTopologyChangedDebounced`, `_knownOnlineDeviceIds` was not checked when evaluating `topPriority`, causing any device state change to switch back to `topPriority` even if the user deliberately selected another audio output.
    * **Fix**: Added `isNewlyConnected = topPriority != null && !_knownOnlineDeviceIds.Contains(topPriority.Id)` guard so auto-switching only triggers if the current default went offline or a higher-priority device was newly connected.

23. **BUG-QA-23: Duplicate `PowerStateChanged` Events & Broken `_syncStream` Reconnection (`ArctisNova7Device.cs`)** — `[FIXED]`
    * **Finding**: `previousOnline` was local to `PollingLoop` and not shared with `AsyncEventReaderLoop`, firing `PowerStateChanged` twice per power transition. Also `EnsureConnected()` returned `true` if `_commandStream != null` even if `_syncStream == null`, preventing real-time `0x45` ChatMix and `0xB9` power interrupts from recovering if the sync stream faulted.
    * **Fix**: Added shared `_lastNotifiedOnline` field and `NotifyPowerState(isOnline)` method under `_ioLock`; ensured `EnsureConnected()` attempts to open `_syncStream` if null even when `_commandStream` is already open; and reduced offline poll interval from 2000ms to 750ms.

24. **BUG-QA-24: 30-Second `EmptyWorkingSet` Hard Page-Fault Stalls (`MainWindow.xaml.cs`)** — `[FIXED]`
    * **Finding**: `MainWindow.xaml.cs` ran a 30-second timer calling `SetProcessWorkingSetSizeEx(-1, -1)` and `EmptyWorkingSet`, forcing the OS kernel to page out the entire process working set every 30 seconds and causing 200–800ms stalls on device switch, ChatMix dial turn, or window wake.
    * **Fix**: Removed the 30-second loop and `EmptyWorkingSet` P/Invoke calls completely. Replaced `TrimMemory` on window hide with non-blocking `GC.Collect(1, GCCollectionMode.Optimized, blocking: false)` without working-set eviction.

25. **BUG-QA-25: UI Thread Blocking on `FileStream.Flush(true)` & Synchronous Device Cycling (`ConfigService.cs`, `DeviceManagerViewModel.cs`)** — `[FIXED]`
    * **Finding**: `ConfigService.Save()` called `fs.Flush(flushToDisk: true)` synchronously on every slider tick and device switch, issuing hardware file flush syscalls that stalled calling threads. In addition, `SwitchNextDevice()` and `SwitchPreviousDevice()` ran synchronously on the UI thread.
    * **Fix**: Debounced `ConfigService.Save()` with a 150ms coalesce timer while providing `SaveImmediate()` for shutdown/export; offloaded `SwitchNextDevice()` and `SwitchPreviousDevice()` to background `Task.Run`.

