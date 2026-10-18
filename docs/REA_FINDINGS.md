# Reverse Engineering Analysis & Protocol Specifications: SteelSeries Arctis Nova 7 & Sonar

This document details the reverse engineering discoveries extracted from the live SteelSeries GG installation (`C:\Program Files\SteelSeries\GG` and `C:\ProgramData\SteelSeries\GG`), SQLite databases, Go binaries (`SteelSeriesEngine.exe`), encrypted OpenPGP `.edevice` GoLisp descriptors (`arctis_nova_7_upgrade_tx.edevice`, `base_arctis_nova_7_gen2_tx.edevice`, `base_arctis_nova_7_tx.edevice`), .NET assemblies (`SteelSeriesSonar.dll`, `SoundStage.*.dll`), and verified via USB HID hardware probing of the connected **SteelSeries Arctis Nova 7 Wireless** transceiver (`VID_1038&PID_22A1`).

---

## 1. Arctis Nova 7 USB HID Specifications

### 1.1 Hardware Identifiers & Transceiver Endpoints
* **Vendor ID (VID):** `0x1038` (SteelSeries ApS)
* **Product IDs (PID) from Decrypted `.edevice` Specifications:**
  * `0x22A1`: Arctis Nova 7 Wireless Transmitter (`arctis_nova_7_upgrade_tx` — Gen 1 hardware upgraded to Gen 2 firmware `2.31/2.31/2.29`, active hardware on this system)
  * `0x227E`: Arctis Nova 7 Gen 2 Wireless Transmitter (`arctis_nova_7_gen2_tx`)
  * `0x2202`: Arctis Nova 7 Gen 1 Wireless Transmitter (`arctis_nova_7_tx`)
  * `0x2206` / `0x220A`: Arctis Nova 7X / 7P Gen 1 Wireless Transmitters
  * `0x229E` / `0x2298`: Arctis Nova 7X / 7P Gen 2 Wireless Transmitters
  * `0x22A5` / `0x22A7`: Arctis Nova 7X / 7P Upgrade Wireless Transmitters
* **USB Composite Topology:**
  * **Interface 0 & 1:** USB Audio Streaming (Audio Class 1.0/2.0 — Speaker/Headphones & Microphone endpoints).
  * **Interface 3 (`MI_03`):**
    * **Collection 01 (Command / Control Interface):**
      * **Usage Page:** `0xFFC0`, **Usage:** `0x0001`
      * **Device Path Pattern:** `\\?\HID#VID_1038&PID_22A1&MI_03&Col01#...`
      * **Transport:** 65-byte USB HID Output Reports (`write`, Report ID `0x00` + 64-byte payload) and 65-byte Input Reports (`read`, Report ID `0x00` + 64-byte response).
    * **Collection 02 (Async Sync Interrupt Interface):**
      * **Usage Page:** `0xFF00`, **Usage:** `0x0001` (`(sync-interface 0xff00 0x0001 5)`)
      * **Device Path Pattern:** `\\?\HID#VID_1038&PID_22A1&MI_03&Col02#...`
      * **Transport:** Asynchronous Input Reports pushed immediately by the dongle when the user turns the physical ChatMix dial or changes headset state.

---

## 2. USB HID Protocol & Opcode Definitions (Decrypted from `.edevice` GoLisp)

By disassembling `github.com/steelseries/engine/pkg/utils/fileencryption.DecryptDeviceFile` (VA `0x1404692c0`) and `DecryptDeviceFile.func1` (VA `0x140469480`) in `SteelSeriesEngine.exe`, we recovered the 128-byte OpenPGP passphrase (`+T0mc(}@GOc4j9]C}K)=J)TS4WnLI0@!=qi5&~xX]#RB#&/ix5O~g[pg7moE4NqZzh{?pw48)NlU$y3^[I#j9DNEMKvkQM2Z6X67RJKwhWsI#m1mu5/Q2ZPF78Kz=U5f`[:128]) and decrypted the exact GoLisp device specifications for the Arctis Nova 7 family.

### 2.1 Does the Nova 7 Require a Host "Enable ChatMix" Command?
* **Verified Finding:** In `standard-boilerplate.edevice`, `(define (enable-chatmix) '())` and `(define (disable-chatmix) '())` are no-ops by default.
* While the **Arctis Nova Pro Wireless** (`base_arctis_nova_pro_wireless.edevice`) overrides `enable-chatmix` to send command `0x49` (`software_chatmix_status`), the **Arctis Nova 7** (`arctis_nova_7_tx.edevice`, `arctis_nova_7_upgrade_tx.edevice`, and `arctis_nova_7_gen2_tx.edevice`) does **not** override `enable-chatmix` and instead registers `MM_DEVICE_FEATURE_SOFTWARE_CHATMIX_FINITE`.
* Its physical ChatMix dial is **always active in hardware** and reports `game_chatmix_level` (`0..100`) and `chat_chatmix_level` (`0..100`) both when polled via `0xB0` and asynchronously via interrupt `0x45` without any host unlock packet.

---

### 2.2 Headset Status, Battery & Hardware ChatMix (`0xB0`)

Defined in `(struct headset_status ...)` inside `base_arctis_nova_7_gen2_tx.edevice` (lines 249–273):

#### Query Request Packet (Host -> Dongle, 65 bytes)
| Byte Offset | Field | Value | Description |
|:---|:---|:---|:---|
| `0` | `report_id` | `0x00` | Windows HID Report ID |
| `1` | `command` | `0xB0` | Headset Status Query |
| `2..64` | Padding | `0x00` | Zero-padded to 65 bytes |

#### Response Packet (Dongle -> Host, 65 bytes including Report ID `0x00`)
| Byte Offset (`resp[i]`) | Field | Range | Authoritative GoLisp Specification |
|:---|:---|:---|:---|
| `0` | `report_id` | `0x00` | Windows HID Report ID (`0x00`) |
| `1` | `command` | `0xB0` | Echo of command (`0xB0`) |
| `2` | `connection_status` | `0..3` | `0`: `NOT_PAIRED_NOT_SEARCHING`<br>`1`: `NOT_PAIRED_SEARCHING`<br>`2`: `PAIRED_NOT_CONNECTED` (Headset Powered OFF)<br>`3`: `PAIRED_CONNECTED` (Headset Powered ON & Linked) |
| `3` | `battery_status` | `1..100` (or `0..4`) | • **Upgrade / Gen 2 (`0x22A1`, `0x227E`):** Direct percentage `1..100%` in 1% steps.<br>• **Legacy Gen 1 (`0x2202`):** `0..4` mapped to `0%, 25%, 50%, 75%, 100%`. |
| `4` | `charging_status` | `0..3` | `0`: `UNKNOWN_OR_HEADSET_NOT_CONNECTED`<br>`1`: `PLUGGED_IN_CHARGING`<br>`2`: `PLUGGED_IN_NOT_CHARGING`<br>`3`: `DISCHARGING` |
| `5` | `game_chatmix_level` | `0..100` | Game ChatMix level (`0..100%`, `100` at center detent) |
| `6` | `chat_chatmix_level` | `0..100` | Chat ChatMix level (`0..100%`, `100` at center detent) |
| `7` | `unused` | `byte[1]` | Reserved |
| `8` | `bt_power_default` | `0..1` | Bluetooth power-on at startup (`0` = Off, `1` = On) |
| `9` | `bt_call_default` | `0..2` | Bluetooth call behavior (`0` = Do nothing, `1` = -12dB, `2` = Mute game) |
| `10` | `mic_mute` | `0..255` | Hardware microphone mute state |

---

### 2.3 Asynchronous Sync Interface Events (`UsagePage 0xFF00`, `Col02`)

Defined in `(define (translate_sync_data payload) ...)` (`base_arctis_nova_7_gen2_tx.edevice` lines 1181–1230). When the user interacts with physical controls on the headset, the dongle emits an input report where `payload[0] = 0x00` (Report ID) and `payload[1]` is the event opcode:

| Event Opcode (`payload[1]`) | Event Type | Payload Bytes | Description |
|:---|:---|:---|:---|
| `0x45` | `chatMixEvent` | `payload[2]` = `game_level` (`0..100`)<br>`payload[3]` = `chat_level` (`0..100`) | Real-time physical ChatMix dial turn |
| `0xB7` | `batteryEvent` | `payload[2]` = `battery_level` | Real-time battery update (implies `connection_status == 3`) |
| `0xB9` | `connectionEvent` | `payload[2]` = `connection_status` (`0..3`) | Headset power ON (`3`) or OFF (`2`) transition |
| `0xBB` | `chargingEvent` | `payload[2]` = `charging_status` (`0..3`) | USB-C charging cable plugged (`1`) or unplugged (`3`) |
| `0x52` | `micStatusEvent` | `payload[3]` = `micEnabled` | Physical mic mute button toggled on headset |
| `0x37` / `0x39` / `0xA3` / `0xAE` | Setting Sync | `payload[2]` = value | Setting change notification |

---

### 2.4 Hardware Settings Readback & Write Opcodes

#### 1. Read Audio Settings (`0x20` — `audio_settings`)
* **Request:** `[0x00, 0x20, 0x00, ...]`
* **Response:**
  * `resp[0] = 0x00` (Report ID)
  * `resp[1] = 0x20` (Command Echo)
  * `resp[2] = mic_volume` (`0..7`, default `7`)
  * `resp[3] = sidetone` (`0..3`: `0`=Off, `1`=Low, `2`=Med, `3`=High)
  * `resp[4] = volume_limiter` (`0..1`: `0`=105dB max, `1`=99dB max)

#### 2. Read UX Settings (`0xA0` — `ux_settings`)
* **Request:** `[0x00, 0xA0, 0x00, ...]`
* **Response:**
  * `resp[0] = 0x00` (Report ID)
  * `resp[1] = 0xA0` (Command Echo)
  * `resp[2] = inactivity_timer` (`0..255` minutes; `0` = Never)
  * `resp[3] = muted_mic_brightness` (`0..3`: `0`=Off, `1`=Low, `2`=Med, `3`=High)

#### 3. Write Hardware Settings & Commit to Flash (`0x09` — `save_to_flash`)
Per `base_arctis_nova_7_gen2_tx.edevice` and `standard-boilerplate.edevice`:
* **Command spacing:** `(define (time-between-commands) 30)` (30 ms between HID writes).
* **Sidetone (`0x39`):** `[0x00, 0x39, <level 0..3>]`
* **Inactivity Sleep Timer (`0xA3`):** `[0x00, 0xA3, <minutes 0..255>]`
* **Muted Mic LED Brightness (`0xAE`):** `[0x00, 0xAE, <level 0..3>]` *(Note: `0x37` is `mic_volume` `0..7`, whereas `0xAE` is `muted_mic_brightness` `0..3`!)*
* **Hardware Mic Volume (`0x37`):** `[0x00, 0x37, <level 0..7>]`
* **EU Volume Limiter (`0x3A`):** `[0x00, 0x3A, <enabled 0..1>]`
* **Save to Non-Volatile Flash (`0x09`):** `[0x00, 0x09, 0x00, ...]` followed by a mandatory **1500 ms delay** (`; Add 1.5s delay after Save as FW will block all commands for 1.5s after Save triggered`).

---

## 3. ChatMix Mathematics & Scaling Logic (Disassembly of `SteelSeriesEngine.exe`)

Reverse engineering of `SteelSeriesEngine.exe` (Go 1.24.3, symbols in `pkg/audio_lite/audio_lite_chatmix`) revealed the exact mathematical pipeline used for ChatMix:

### 3.1 Raw Values to Scalar Attenuation
In `applyAttenuation` (VA `0x1404fa100`):
$$\text{gameGainScaled} = \frac{\text{raw\_game}}{100.0} \in [0.0, 1.0]$$
$$\text{chatGainScaled} = \frac{\text{raw\_chat}}{100.0} \in [0.0, 1.0]$$

### 3.2 Attenuation to Normalized Balance $C \in [-1.0, +1.0]$
In `toBalance` (VA `0x1404fa2c0`):
* If $\text{chatGainScaled} < 1.0$:
  $$C = \text{chatGainScaled} - 1.0 \quad (C \in [-1.0, 0.0] \text{, favoring Game})$$
* If $\text{gameGainScaled} < 1.0$:
  $$C = 1.0 - \text{gameGainScaled} \quad (C \in [0.0, +1.0] \text{, favoring Chat})$$
* Center detent ($\text{game} = 100, \text{chat} = 100$):
  $$C = 0.0 \quad (M_{\text{game}} = 1.0, M_{\text{chat}} = 1.0)$$

### 3.3 Dynamic Session Volume Application
For any audio session $S$ managed by WASAPI:
* **If routed to Game channel:**
  $$V_S = V_{\text{master}} \times V_{\text{game}} \times \min(1.0, 1.0 - C)$$
* **If routed to Chat channel:**
  $$V_S = V_{\text{master}} \times V_{\text{chat}} \times \min(1.0, 1.0 + C)$$

---

## 4. Sonar App Categorization & Routing Map

From `C:\ProgramData\SteelSeries\GG\apps\sonar\db\database.db` and `SoundStage.Services.dll`, exported to [`Resources/default_app_categories.json`](file:///C:/projects/titaniumseeries/Resources/default_app_categories.json):
* **Voice / Communication (`CHAT`):** `discord.exe`, `ms-teams.exe`, `msteams.exe`, `slack.exe`, `zoom.exe`, `telegram.exe`, `skype.exe`, `whatsapp.exe`, `teamspeak3.exe`, `ts3client_win64.exe`, `signal.exe`, `element.exe`.
* **Media Players & Browsers (`GAME`):** `spotify.exe`, `itunes.exe`, `vlc.exe`, `foobar2000.exe`, `musicbee.exe`, `tidal.exe`, `aimp.exe`, `chrome.exe`, `msedge.exe`, `firefox.exe`, `brave.exe`, `opera.exe`, `operagx.exe`, `vivaldi.exe`.
* **Gaming Platforms & Games (`GAME`):** `steam.exe`, `epicgameslauncher.exe`, `riotclientservices.exe`, `valorant.exe`, `cs2.exe`, `leagueclient.exe`, etc.
* **Unclassified Apps:** Appear in `"Apps to be routed"` under the Master column until dragged or clicked into `GAME` or `CHAT`.

---

## 5. Verification Status
* **Statically Verified (100% Authoritative from Decrypted `.edevice` Firmware Specs):**
  * All command opcodes (`0xB0`, `0x20`, `0xA0`, `0x39`, `0xA3`, `0xAE`, `0x37`, `0x3A`, `0x09`), byte offsets, value ranges, flash save timing (1500 ms), and async interrupt events (`0x45`, `0xB7`, `0xB9`, `0xBB`, `0x52`) are directly verified from SteelSeries' own decrypted `.edevice` GoLisp definitions.
* **Live Hardware Verification (Headset Offline Probe Completed; Online Test Pending):**
  * Verified `0xB0` query/response against the physical USB dongle (`VID_1038&PID_22A1&MI_03`) while the headset was powered off (`00 B0 02 35 00 64 64 00 00 01 ...`).
  * Live interactive testing with the headset powered ON (turning the ChatMix wheel, verifying Sidetone/LED changes) can be performed as soon as the headset is powered on.
