# Reverse Engineering Analysis & Protocol Specifications: SteelSeries Arctis Nova 7 & Sonar

This document details the reverse engineering discoveries extracted from the live SteelSeries GG installation (`C:\Program Files\SteelSeries\GG` and `C:\ProgramData\SteelSeries\GG`), SQLite databases, Go binaries (`SteelSeriesEngine.exe`), .NET assemblies (`SteelSeriesSonar.dll`, `SoundStage.*.dll`), and verified via live USB HID hardware probing of the connected **SteelSeries Arctis Nova 7 Wireless** transceiver.

---

## 1. Arctis Nova 7 USB HID Specifications

### 1.1 Hardware Identifiers & Transceiver Endpoints
* **Vendor ID (VID):** `0x1038` (SteelSeries ApS)
* **Product IDs (PID):**
  * `0x2202`: Arctis Nova 7 Wireless Transmitter (Gen 1)
  * `0x227E`: Arctis Nova 7 Wireless Transmitter (Gen 2)
  * `0x22A1`: Arctis Nova 7 Wireless Transmitter (Gen 2 Upgrade / Current Hardware)
  * Other Nova 7 variants (7X / 7P / WoW / Diablo IV editions) use different PIDs in the GG database; they were **not** verified and are not supported by NovaSonics.
* **USB Composite Topology:**
  * **Interface 0 & 1:** USB Audio Streaming (Audio Class 1.0/2.0 — Speaker/Headphones & Microphone endpoints).
  * **Interface 3 (Control Interface):**
    * **Usage Page:** `0xFFC0` (Vendor-defined audio/telephony)
    * **Usage:** `0x0001`
    * **Device Path Pattern:** `\\?\HID#VID_1038&PID_22A1&MI_03#...#{4d1e55b2-f16f-11cf-88cb-001111000030}`
    * **Transport:** USB HID Output Reports (`write`, Report ID `0x00`) and Input Reports (`read`, 64 bytes).
  * **Interface 4:** Consumer Control (`Usage Page: 0x000C`, `Usage: 0x0001` — Media keys, Volume dial).
  * **Interface 5:** Vendor Control (`Usage Page: 0xFF00`, `Usage: 0x0001` — Secondary telemetry).

---

## 2. USB HID Protocol & Opcode Definitions

All commands on Interface 3 use 64-byte packets with Report ID prefix `0x00`.

### 2.1 Headset Status, Battery & Hardware ChatMix (`0xB0`)

#### Query Request Packet (Host -> Dongle)
| Byte Offset | Field | Value | Description |
|:---|:---|:---|:---|
| `0` | Report ID | `0x00` | Mandatory Windows HID Report ID |
| `1` | Opcode | `0xB0` | Status & Battery Polling Opcode |
| `2..64` | Padding | `0x00` | Zero-padded to 65 bytes total |

#### Response Packet (Dongle -> Host, 64 bytes)
| Byte Offset | Field | Type | Description / Decoded Values |
|:---|:---|:---|:---|
| `0` | Opcode Echo | `uint8` | Echo of command (`0xB0` = `176`) |
| `1` | Connection State | `uint8` | Wireless link status:<br>• `0x03`: Headset Powered ON & Linked (Online)<br>• `0x02`: Headset Powered OFF / Standby (Offline)<br>• `0x00`: Unpaired / Dongle Standby |
| `2` | Battery Level | `uint8` | Battery charge percentage `0..100` (e.g. `0x35` = 53%) |
| `3` | Sub-status | `uint8` | Reserved / Firmware state (`0x00`) |
| `4` | Game Attenuation | `uint8` | Current Game volume channel position (`0..100`, `100` = full volume) |
| `5` | Chat Attenuation | `uint8` | Current Chat volume channel position (`0..100`, `100` = full volume) |
| `6..7` | Reserved | `uint8[2]`| `0x00, 0x00` |
| `8` | Charging State | `uint8` | Charging status flag:<br>• `0x01`: Headset plugged into USB power (Charging)<br>• `0x00`: Operating on battery power (Discharging) |
| `9..63` | Reserved | `uint8[55]`| `0x00...` |

> [!NOTE]
> Physical ChatMix wheel position is transmitted directly in bytes `4` (Game) and `5` (Chat) of the `0xB0` report. At center detent (50/50 balance), both bytes report `100` (`0x64`). When turned toward Game, Chat attenuation decreases (`chat < 100`). When turned toward Chat, Game attenuation decreases (`game < 100`).

---

### 2.2 Hardware Settings Configuration Opcodes

Hardware configuration packets are written via Output Reports on Interface 3:

#### 1. Sidetone Level (`0x39`)
Controls the hardware microphone monitoring loopback into the headset speakers.
```
Packet: [0x00, 0x39, <level>]
```
* `<level> = 0x00`: Off
* `<level> = 0x01`: Low
* `<level> = 0x02`: Medium
* `<level> = 0x03`: High

#### 2. Inactive Sleep Timer (`0xA3`)
Controls auto-power-off delay when no audio is playing.
```
Packet: [0x00, 0xA3, <minutes>]
```
* `<minutes> = 0x00`: Never
* `<minutes> = 0x05`: 5 minutes (`0x05`)
* `<minutes> = 0x0F`: 15 minutes (`0x0F`)
* `<minutes> = 0x1E`: 30 minutes (`0x1E`)
* `<minutes> = 0x3C`: 60 minutes (`0x3C`)

#### 3. Mic Mute LED Brightness (`0x37`)
Controls the red indicator LED on the retractable ClearCast microphone tip when muted.
```
Packet: [0x00, 0x37, <level>]
```
* `<level> = 0x00`: Off
* `<level> = 0x01`: Low
* `<level> = 0x02`: Medium
* `<level> = 0x03`: High

#### 4. Hardware Model / Firmware Query (`0x20`)
```
Request:  [0x00, 0x20]
Response: [0x20, 0x07, 0x00, ...] (0x07 indicates Nova 7 family)
```

---

## 3. ChatMix Mathematics & Scaling Logic (Disassembly of `SteelSeriesEngine.exe`)

Reverse engineering of the Go binary `SteelSeriesEngine.exe` (Go 1.24.3, symbols in `pkg/audio_lite/audio_lite_chatmix` and `pkg/chatmix`) revealed the exact mathematical pipeline used for ChatMix:

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

From the SQLite database `C:\ProgramData\SteelSeries\GG\apps\sonar\db\database.db`, `key_value` table, and `SoundStage.Services.dll`:

### 4.1 Audio Process Exclusion List (`AUDIO_PROCESS_EXCLUSION_LIST`)
System audio processes that should **never** be routed or intercepted:
* `SteelSeriesSonar.exe`
* `SteelSeriesGGClient.exe`
* `SteelSeriesEngine.exe`
* `SteelSeriesCaptureSvc.exe`
* `SteelSeriesCVGameSense.exe`
* `SteelSeriesPrism.exe`
* `audiodg.exe` (Windows Audio Device Graph Isolation)
* `rundll32.exe`
* `systemsettings.exe`
* `searchapp.exe`
* `shellexperiencehost.exe`
* `startmenuexperiencehost.exe`
* `explorer.exe`
* `dwm.exe` (Desktop Window Manager)
* `svchost.exe`
* `taskmgr.exe`

### 4.2 Error-Prone Processes (`AUDIO_PROCESS_ROUTING_ERROR_PRONE_LIST`)
Applications known to lock their audio endpoints or require clean session handling:
* `ms-teams.exe` / `msteams.exe`
* `Discord.exe`
* `iTunes.exe`

### 4.3 Default Routing Dictionary
Exported to [`Resources/default_app_categories.json`](file:///C:/projects/titaniumseeries/Resources/default_app_categories.json):
* **Voice / Communication (`CHAT`):** `discord.exe`, `ms-teams.exe`, `msteams.exe`, `slack.exe`, `zoom.exe`, `telegram.exe`, `skype.exe`, `whatsapp.exe`, `teamspeak3.exe`, `ts3client_win64.exe`, `signal.exe`, `element.exe`.
* **Media Players (`GAME`/`MEDIA`):** `spotify.exe`, `itunes.exe`, `vlc.exe`, `foobar2000.exe`, `musicbee.exe`, `tidal.exe`, `aimp.exe`.
* **Browsers:** `chrome.exe`, `msedge.exe`, `firefox.exe`, `brave.exe`, `opera.exe`, `operagx.exe`, `vivaldi.exe`.
* **Gaming Platforms & Games:** `steam.exe`, `epicgameslauncher.exe`, `riotclientservices.exe`, `valorant.exe`, `cs2.exe`, `leagueclient.exe`, etc.

---

## 5. Live Physical Verification Summary
* Verified against physical USB transmitter `USB\VID_1038&PID_22A1&MI_03`:
  * Output Report write to opcode `0xB0` returned 64-byte payload:
    `B0 02 35 00 64 64 00 00 01 ...`
  * Decoded: `Opcode=0xB0`, `State=0x02` (Offline/Standby), `Battery=53%`, `GameAtt=100`, `ChatAtt=100`, `Charging=True`.
  * **UNVERIFIED:** hardware opcodes `0x39` / `0xA3` / `0x37` were written to the device but returned no response, so their effect and value encodings are not confirmed. ChatMix bytes 4/5 and the battery scale are inferred; the headset was powered off during the test (values 100/100). Needs live capture with the headset on.

Phase 1 is **partially complete**: static findings are documented, but the live-hardware items above still need capture with the headset powered on. Do not uninstall GG until they are confirmed.
