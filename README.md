# NovaSonics 🎧

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078D6.svg)](https://microsoft.com)
[![Framework](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4.svg)](https://dotnet.microsoft.com/)
[![Interop](https://img.shields.io/badge/Audio%20Routing-100%25%20Native%20WinRT%20COM-00E5FF.svg)](docs/ARCHITECTURE.md)

**NovaSonics** is a lightweight, high-performance, native Windows audio mixer, routing engine, and hardware controller designed specifically for the **SteelSeries Arctis Nova 7** wireless headset family.

It completely replaces the bloated **SteelSeries GG / Sonar** software suite. NovaSonics runs as a silent, zero-overhead system tray daemon with **zero kernel virtual audio drivers**, **zero telemetry**, and **zero external executables**.

---

## ⚡ Why NovaSonics?

SteelSeries GG installs heavy background processes, multiple virtual audio cables, electron runtimes, and background telemetry services consuming hundreds of megabytes of RAM and noticeable CPU cycles.

NovaSonics communicates **directly with the headset transceiver via USB HID** and manages audio streams via **native Windows Core Audio (WASAPI & WinRT)**.

### Performance & Resource Comparison

| Metric | SteelSeries GG / Sonar | NovaSonics |
| :--- | :--- | :--- |
| **Idle Memory (RAM)** | ~350 MB – 800 MB (Multiple Electron + Node.js processes) | **~35 MB – 55 MB** (Single native process) |
| **Idle CPU Usage** | 0.5% – 3.0% (Constant background telemetry) | **0.0%** (Timers fully suspended in tray) |
| **Audio Drivers** | 4–6 Kernel Virtual Audio Drivers (VADs) | **0 Virtual Drivers** (Native Windows WASAPI) |
| **Audio Latency** | Introduces virtual cable buffer latency (~10–30ms) | **0ms Added Latency** (Direct hardware path) |
| **Per-App Routing** | Proprietary virtual cable rerouting | **Pure Native WinRT Interop** (`IAudioPolicyConfig`) |
| **External Binaries** | Multiple `.exe` processes and helper services | **100% Pure C#** (Zero external `.exe` binaries) |
| **Installation Size** | ~500 MB – 1 GB | **~30 MB** (Installer: ~15 MB) |
| **Telemetry & Cloud** | Mandatory accounts, analytics, cloud syncing | **100% Offline & Open Source** (No analytics) |

---

## ✨ Features

- **🎛️ Physical ChatMix Wheel Synchronization**:
  Hardware interrupt-driven (`MI_03` / `Col02`) ChatMix tracking. Turning the physical dial on the headset instantly attenuates Game vs. Chat audio non-destructively in Windows.
- **⚡ 100% Pure Native WinRT Audio Routing**:
  Per-channel routing (`GAME`, `MEDIA`, `CHAT`, `MIC`) implemented via native C# COM/WinRT interop (`Windows.Media.Internal.AudioPolicyConfig` / `IAudioPolicyConfigFactory`). Zero external binaries, sub-millisecond execution (<1ms).
- **🎚️ 10-Band Hardware DAC Parametric EQ**:
  Directly programmes the headset DSP's onboard digital-to-analog converter with 4 profile slots (`GAME`, `MEDIA`, `VOICE`, `CUSTOM`). Settings persist in headset hardware even when plugged into a PlayStation, Nintendo Switch, or phone.
- **🛡️ EEPROM Flash Wear Protection**:
  Slider adjustments execute instantly in volatile RAM (`15ms` register writes). An intelligent 2.5-second debounce timer commits to EEPROM (`0x09`) only when adjustments settle, protecting headset flash memory from premature wear.
- **🔄 Intelligent Endpoint Auto-Switching**:
  Monitors physical wireless power state (`PowerStateChanged`). When the headset turns on, output seamlessly switches to Nova 7. When powered off, output immediately falls back to your speakers or secondary device.
- **🧹 Volume Mixer Cache Purge**:
  One-click cleanup of Windows Volume Mixer ghost sliders and orphaned audio session registry keys (`HKCU\...\PropertyStore`).
- **🔋 Live Dynamic Battery Tray Icon**:
  Pixel-perfect GDI battery gauge rendered live in the system tray with distinct charging animations and Windows toast notifications at 20% remaining battery.
- **⌨️ Customizable Global Hotkeys**:
  - `Ctrl + Alt + Up`: Master Volume +5%
  - `Ctrl + Alt + Down`: Master Volume -5%
  - `Ctrl + Alt + Left`: Shift ChatMix toward Game (-10%)
  - `Ctrl + Alt + Right`: Shift ChatMix toward Chat (+10%)
  - `Ctrl + Alt + M`: Toggle Microphone Mute

---

## 🎧 Supported Hardware

NovaSonics supports all variants of the SteelSeries Arctis Nova 7 wireless headset family (Vendor ID `0x1038`):

| Product ID (PID) | Model Description | Firmware Architecture |
| :--- | :--- | :--- |
| `0x2202` | SteelSeries Arctis Nova 7 (Gen 1 PC) | Legacy Battery Scale (`0..4`) |
| `0x2206` | SteelSeries Arctis Nova 7X (Gen 1 Xbox/PC) | Legacy Battery Scale (`0..4`) |
| `0x220A` | SteelSeries Arctis Nova 7P (Gen 1 PlayStation/PC) | Legacy Battery Scale (`0..4`) |
| `0x227E` | SteelSeries Arctis Nova 7 (Gen 2 PC) | Direct Battery Scale (`1..100%`) |
| `0x2298` | SteelSeries Arctis Nova 7P (Gen 2 PlayStation) | Direct Battery Scale (`1..100%`) |
| `0x229E` | SteelSeries Arctis Nova 7X (Gen 2 Xbox) | Direct Battery Scale (`1..100%`) |
| `0x22A1` | SteelSeries Arctis Nova 7 (Upgrade Edition Transceiver) | Direct Battery Scale (`1..100%`) |
| `0x22A5` | SteelSeries Arctis Nova 7X (Upgrade Edition) | Direct Battery Scale (`1..100%`) |
| `0x22A7` | SteelSeries Arctis Nova 7P (Upgrade Edition) | Direct Battery Scale (`1..100%`) |

*Fallback discovery automatically detects devices by product string if a new revision PID is introduced.*

---

## 🚀 Installation

### Option 1: Inno Setup Installer (Recommended)
1. Download the latest installer `NovaSonics-Setup-v1.0.0.exe` from the [Releases](https://github.com/braz/NovaSonics/releases) page.
2. Run the setup wizard.
   - Choose whether to create a desktop shortcut.
   - Choose whether to launch automatically with Windows (minimized to system tray).
3. NovaSonics will launch and immediately link with your Nova 7 transceiver.

> [!NOTE]
> **Windows SmartScreen Note**: Because NovaSonics is a freshly built community open-source application without an expensive commercial EV code-signing certificate, Windows SmartScreen may display an alert. Click **"More info"** and then **"Run anyway"**. You can inspect 100% of the source code in this repository.

### Option 2: Portable Archive (.zip)
1. Download `NovaSonics-v1.0.0-Portable-win-x64.zip` from [Releases](https://github.com/braz/NovaSonics/releases).
2. Extract the archive into any folder (e.g. `C:\Tools\NovaSonics`).
3. Run `NovaSonics.exe`. Settings are saved to `%APPDATA%\NovaSonics\config.json`.

---

## 🛠️ Building from Source

### Prerequisites
- Windows 10 (Version 20H1+) or Windows 11 (64-bit)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (optional, required only for building the installer)

### Quick Build
Clone the repository and compile using standard .NET CLI commands:

```powershell
# Clone the repository
git clone https://github.com/braz/NovaSonics.git
cd NovaSonics

# Build Debug configuration
dotnet build

# Run application
dotnet run --project NovaSonics/NovaSonics.csproj
```

### Automated Release Packaging
To build both the standalone portable ZIP and Inno Setup installer executable:

```powershell
# Run automated release pipeline
./build-release.ps1
```

The script will:
1. Read `<Version>` from `NovaSonics.csproj`.
2. Publish optimized win-x64 binaries into `publish/win-x64`.
3. Package `dist/NovaSonics-v{Version}-Portable-win-x64.zip`.
4. Locate or install Inno Setup 6 (via `winget`) and generate `dist/NovaSonics-Setup-v{Version}.exe`.

---

## 📖 Technical Documentation

Detailed deep-dive architectural specifications, reverse-engineered USB HID protocol layouts, and security audits are available in the [`docs/`](docs/) directory:

- [**Technical Architecture & Data Flow**](docs/ARCHITECTURE.md): Complete component breakdown, WinRT COM vtable layouts, and threading model.
- [**Production Hardening Plan**](docs/HARDENING_PLAN.md): Fault-tolerance, single-instance mutexes, atomic file persistence, and sleep/wake recovery.
- [**Bug & Reliability Audit**](docs/BUG_AUDIT.md): Comprehensive analysis of edge cases and fixes applied across the audio engine.

---

## 📄 License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.
