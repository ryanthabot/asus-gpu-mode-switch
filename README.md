# ASUS GPU Mode Switch — "Go Time" & "Eco Mode"

Two one-click executables that switch the **GPU Mode** on ASUS laptops — the same
Standard / Eco switch that lives in *Armoury Crate → Devices → System Settings →
GPU Performance* — without opening Armoury Crate at all.

| Executable | What it does |
|---|---|
| **Go Time.exe** | Standard GPU mode: dGPU enabled, hybrid (MSHybrid) display path |
| **Eco Mode.exe** | Eco GPU mode: the dGPU is completely powered off (battery / silence) |

> **v1.0.12** — **Energy Saver now uses the documented power setting** (no
> more Quick Settings popup): Eco Mode sets the Energy Saver battery threshold
> to **100%** (Energy Saver always engages when on battery) and Go Time sets
> it to **0%** (never auto-engages). Written via
> `PowerWriteDCValueIndex` on the active scheme and applied immediately with
> `PowerSetActiveScheme`. The current threshold is shown in `--status`, and
> every return code and read-back is logged. The Quick Settings UI
> automation from v1.0.11 was removed.
>
> **v1.0.11** — Energy Saver toggle via Quick Settings UI Automation
> (removed in v1.0.12; the tile could not be located reliably and the
> popup was unwanted).
>
> **v1.0.10** — **Windows 11 Energy Saver is now synced with the mode**: Eco
> Mode turns Energy Saver **on**, Go Time turns it **off**.
>
> **v1.0.9** — **one-click is back**: launching an app probes and applies
> immediately, with the themed window and shimmer animation running the whole
> time. The confirm step still exists as an opt-in: run with `--confirm` to
> review the detected state and press Apply first.
>
> **v1.0.8** — themed UI: borderless window with the app logo, animated
> shimmer bar, fade-in, background-thread switching; confirm flow moved to
> opt-in (`--confirm`); Go Time icon rebuilt with true transparent corners
> and a gradient 3D tile.
>
> **v1.0.7** — the main window and the diagnostic log window now appear on
> the taskbar (with the app's own NVIDIA-eye / leaf icon), instead of being
> hidden while the switch runs.
>
> **v1.0.6** — application icons added: **Go Time** carries the NVIDIA eye on a
> dark tile (rendered from the official glyph), **Eco Mode** a white leaf on
> green. Both embedded as multi-size `.ico` (16/32/48/256).
>
> **v1.0.5** — fixed MUX misdetection diagnosed from a G513QR log: a bare-zero
> DSTS response (no status bits) now correctly means *"device not implemented
> by this firmware"* instead of *"MUX in dGPU-direct mode"*. On MUX-less
> machines like the G513QR the apps now skip the MUX entirely and do a pure
> live dGPU power toggle — no MUX writes, no restart flow.
>
> **v1.0.4** — the live switch is now **always attempted first**, exactly like
> Armoury Crate: just flip the dGPU power flag, no restart. The MUX/one-time
> restart flow was demoted to a fallback that only kicks in if the firmware
> itself refuses the write while the display path is physically running on
> the dGPU.
>
> **v1.0.3** — Standard ↔ Eco applies **live, without a restart**: the NVIDIA
> Display Container driver service is released before switching to Eco (so the
> firmware can cut dGPU power immediately) and restarted after switching back
> to Standard (so the GPU returns right away).
>
> **v1.0.2** — safe two-step fallback + full diagnostic logging
> (*View log → Copy log*, plus `%LOCALAPPDATA%\GpuModeSwitch\`).
>
> **v1.0.1** — switched from the `ASUS_WMI` WMI class to the direct ACPI device
> (`\\.\ATKACPI`) that modern firmware actually uses. This fixes the
> *"ASUS hardware interface not found"* error on models like the
> **ROG Strix G15 (G513QR)**. The WMI classes remain as fallback.

## Download

Grab both executables from the
[**Releases**](../../releases/latest) page — individually or as a single zip.

## Requirements

- An **ASUS laptop with a dedicated GPU** (ROG, Zephyrus, Strix, TUF, Vivobook Pro, Zenbook Pro…)
- Windows 10 or 11 (nothing else needs to be installed — the apps use the .NET
  Framework that ships with Windows)
- The **ASUS System Control Interface** drivers must be present. They ship with
  Armoury Crate or MyASUS, so if either of those has ever been installed, you're set.
- Administrator rights — a UAC prompt when you double-click is expected and normal.

## How to use

1. Close games and other apps that are using the dGPU.
2. Double-click **Go Time.exe** or **Eco Mode.exe**, confirm the UAC prompt.
3. Done — the app probes, applies the switch live (no restart) and shows the
   result, all in one click. The shimmer bar tracks the whole cycle.
4. Run the other app whenever you want to switch back.

Optional: run with `--confirm` to review the detected state and press
**Apply** before anything is switched.

A restart is only ever needed in one situation: if the firmware refuses the
switch because the display path is physically running through the dGPU
(Ultimate mode). The app detects that, moves the MUX back to hybrid, and asks
for **one** restart — after that, switching is instant every time.

**When something goes wrong:** click **View log** in the app — it shows every
probe, read and write with raw hex values. *Copy log* puts it on the clipboard
so you can paste it into a bug report. The same log is written to
`%LOCALAPPDATA%\GpuModeSwitch\EcoMode.log` (or `GoTime.log`).

Run either app with `--status` (e.g. from a terminal) to see the detected
hardware interface and the current GPU state without switching anything.

## How it works

Armoury Crate is just a UI on top of a BIOS-level switch. Both apps call that
switch directly, trying these channels in order until one answers:

1. **Direct ACPI device I/O** — `\\.\ATKACPI` via `DeviceIoControl`
   (control code `0x0022240C`, methods `DSTS` = read / `DEVS` = write).
   This is what G513QR-class firmware uses and is the primary path.
2. **WMI class `AsusAtkWmi_WMNB`** (`root\WMI`) — older ATK-era firmware.
3. **WMI class `ASUS_WMI`** (`root\WMI`) — other firmware generations.

The device IDs being switched (documented by the Linux kernel `asus-wmi`
driver and used by G-Helper):

| Device ID | Function | Values |
|---|---|---|
| `0x00090020` | dGPU power (Vivobook: `0x00090120`) | 0 = on, 1 = off (eco) |
| `0x00090016` | GPU MUX (Vivobook: `0x00090026`) | 0 = dGPU direct, 1 = Optimus/hybrid |

Both apps keep the MUX on the hybrid path and toggle dGPU power — exactly what
Armoury Crate's *Standard* and *Eco* cards do. The apps auto-detect which
endpoint pair your firmware implements and verify the change by reading the
state back. If Armoury Crate is installed, its UI will show the new mode the
next time you open it.

References: the [Linux kernel `asus-wmi` driver](https://github.com/torvalds/linux/blob/master/include/linux/platform_data/x86/asus-wmi.h)
(which documents these device IDs) and [G-Helper](https://github.com/seerge/g-helper)
(the open-source ASUS control app that uses the same interface on Windows).

## Troubleshooting

- **"ASUS hardware interface not found"** — the ASUS System Control Interface
  driver isn't answering. Install or *repair* Armoury Crate (or MyASUS →
  customer service → driver updates), reboot, and try again. Run the app with
  `--status` to see exactly which channels were tried.
- **Switch doesn't stick / "refused"** — something is still using the dGPU
  (game, browser with hardware acceleration, XG Mobile). Close it and retry.
- **"ASUS WMI interface not found" on a desktop or another brand** — expected;
  this is laptop firmware, it doesn't exist there.
- **Windows SmartScreen warning on first run** — the exes are unsigned.
  Click *More info* → *Run anyway*.
- **Stays in the old mode until you restart** — by design; the MUX/power change
  finalizes on reboot.
- **Reporting a bug** — click *View log → Copy log* in the app and paste the
  output into your issue; it contains everything needed to diagnose remotely.

## Building from source

`src\build.cmd` — that's it. It compiles both executables with the C# compiler
that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`),
so no Visual Studio or .NET SDK is needed.

```
asus-gpu-mode-switch/
├── src/
│   ├── GpuModeSwitch.cs   ← single source, both apps
│   ├── app.manifest       ← requires administrator
│   └── build.cmd          ← builds dist\Go Time.exe + dist\Eco Mode.exe
├── LICENSE (MIT)
└── README.md
```

## Limitations

- Laptops only — desktop motherboards don't expose this interface.
- The "Optimized" (Advanced Optimus auto-switch) mode is not supported;
  these apps switch between Standard and Eco only.
- Model-specific quirks exist. If your machine reports the interface as missing
  or refuses writes even with everything closed, open an issue with the output
  of `Eco Mode.exe --status`.

## License

[MIT](LICENSE)
